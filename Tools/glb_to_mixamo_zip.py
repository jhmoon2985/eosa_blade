"""
Converts a GLB (e.g. from TRELLIS / Hunyuan3D) into a Mixamo-ready ZIP
containing OBJ + MTL + PNG albedo texture with:
  1. Spatial vertex welding (preserves exact UVs via separate vt indices; prevents 200+ UV-scrap shells)
  2. Internal stitching of detached boot/limb shells inside the robe/armor (single solid mesh, ZERO robe cutting)
  3. Y-up, feet at y=0, centered X/Z, UV V-flipped (glTF -> OBJ).

Usage:
  python glb_to_mixamo_zip.py <input.glb> <output_dir> <base_name> [target_height_m] [--flip-y]
"""
import sys
import os
import json
import struct
import zipfile
from collections import deque
import numpy as np


def _get_components(v_faces: np.ndarray, n_verts: int):
    adj = [[] for _ in range(n_verts)]
    for a, b, c in v_faces:
        adj[a].extend((b, c))
        adj[b].extend((a, c))
        adj[c].extend((a, b))
    vis = [False] * n_verts
    comps = []
    for i in range(n_verts):
        if not vis[i] and adj[i]:
            q = deque([i])
            vis[i] = True
            nodes = []
            while q:
                u = q.popleft()
                nodes.append(u)
                for w in adj[u]:
                    if not vis[w]:
                        vis[w] = True
                        q.append(w)
            comps.append(nodes)
    comps.sort(key=len, reverse=True)
    return comps


def convert_glb(glb_path: str, out_dir: str, base_name: str, target_height: float = 1.78, flip_y_180: bool = False):
    os.makedirs(out_dir, exist_ok=True)
    with open(glb_path, "rb") as f:
        data = f.read()

    magic, _, _ = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise ValueError(f"Not a valid GLB file: {glb_path}")

    c0_len, _ = struct.unpack_from("<II", data, 12)
    gltf = json.loads(data[20 : 20 + c0_len].decode("utf-8"))
    bin_off = 20 + c0_len
    c1_len, _ = struct.unpack_from("<II", data, bin_off)
    bin_data = data[bin_off + 8 : bin_off + 8 + c1_len]

    comp_map = {
        5120: np.int8,
        5121: np.uint8,
        5122: np.int16,
        5123: np.uint16,
        5125: np.uint32,
        5126: np.float32,
    }
    type_cnt_map = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}

    def read_accessor(acc_idx: int) -> np.ndarray:
        acc = gltf["accessors"][acc_idx]
        bv = gltf["bufferViews"][acc["bufferView"]]
        offset = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
        count = acc["count"]
        dtype = comp_map[acc["componentType"]]
        tcnt = type_cnt_map[acc["type"]]
        stride = bv.get("byteStride", 0)
        elem_size = np.dtype(dtype).itemsize * tcnt
        if stride and stride != elem_size:
            arr = np.empty((count, tcnt), dtype=dtype)
            for i in range(count):
                arr[i] = np.frombuffer(bin_data, dtype=dtype, count=tcnt, offset=offset + i * stride)
            return arr
        return np.frombuffer(bin_data, dtype=dtype, count=count * tcnt, offset=offset).reshape(count, tcnt).copy()

    prim = gltf["meshes"][0]["primitives"][0]
    raw_pos = read_accessor(prim["attributes"]["POSITION"]).astype(np.float64)
    raw_uv = read_accessor(prim["attributes"]["TEXCOORD_0"]).astype(np.float64)
    raw_faces = read_accessor(prim["indices"]).reshape(-1, 3).astype(np.int32)

    if flip_y_180:
        raw_pos[:, 0] = -raw_pos[:, 0]
        raw_pos[:, 2] = -raw_pos[:, 2]

    # 1. Weld spatial vertices while preserving exact UVs via separate vt indices
    rounded = np.round(raw_pos * 10000.0).astype(np.int64)
    uniq, inv = np.unique(rounded, axis=0, return_inverse=True)
    n_w = len(uniq)
    wpos = np.zeros((n_w, 3), dtype=np.float64)
    wcnt = np.zeros(n_w, dtype=np.float64)
    np.add.at(wpos, inv, raw_pos)
    np.add.at(wcnt, inv, 1.0)
    wpos /= wcnt[:, None]

    v_faces = inv[raw_faces]
    valid = (v_faces[:, 0] != v_faces[:, 1]) & (v_faces[:, 1] != v_faces[:, 2]) & (v_faces[:, 2] != v_faces[:, 0])
    v_faces = v_faces[valid]
    uv_faces = raw_faces[valid]

    # 2. Drop tiny floating specks (<30 verts) & internally stitch detached boots onto body (zero robe cutting!)
    comps = _get_components(v_faces, n_w)
    sig = [c for c in comps if len(c) >= 30]
    keep_v = np.zeros(n_w, dtype=bool)
    for c in sig:
        keep_v[c] = True
    f_keep = keep_v[v_faces[:, 0]] & keep_v[v_faces[:, 1]] & keep_v[v_faces[:, 2]]
    v_faces = v_faces[f_keep]
    uv_faces = uv_faces[f_keep]

    v_to_uv = np.zeros(n_w, dtype=np.int32)
    for vf, uf in zip(v_faces, uv_faces):
        v_to_uv[vf[0]] = uf[0]
        v_to_uv[vf[1]] = uf[1]
        v_to_uv[vf[2]] = uf[2]

    main_nodes = np.array(sig[0], dtype=np.int32)
    main_pts = wpos[main_nodes]
    extra_vf, extra_uf = [], []
    for comp in sig[1:]:
        cnodes = np.array(comp, dtype=np.int32)
        top_nodes = cnodes[np.argsort(-wpos[cnodes, 1])[: min(16, len(cnodes))]]
        cent = wpos[top_nodes].mean(axis=0)
        angles = np.arctan2(wpos[top_nodes, 2] - cent[2], wpos[top_nodes, 0] - cent[0])
        ring_a = top_nodes[np.argsort(angles)]
        ring_b = [int(main_nodes[np.argmin(np.sum((main_pts - wpos[va]) ** 2, axis=1))]) for va in ring_a]
        m = len(ring_a)
        for i in range(m):
            a0, a1, b0, b1 = int(ring_a[i]), int(ring_a[(i + 1) % m]), ring_b[i], ring_b[(i + 1) % m]
            if len({a0, a1, b0}) == 3:
                extra_vf.append([a0, a1, b0])
                extra_uf.append([v_to_uv[a0], v_to_uv[a1], v_to_uv[b0]])
            if len({a1, b1, b0}) == 3:
                extra_vf.append([a1, b1, b0])
                extra_uf.append([v_to_uv[a1], v_to_uv[b1], v_to_uv[b0]])
    if extra_vf:
        v_faces = np.vstack([v_faces, np.array(extra_vf, dtype=np.int32)])
        uv_faces = np.vstack([uv_faces, np.array(extra_uf, dtype=np.int32)])

    # 3. Normalize scale to target_height (meters), feet on Y=0, centered on X/Z
    used = np.unique(v_faces)
    min_y, max_y = wpos[used, 1].min(), wpos[used, 1].max()
    scale = target_height / max(1e-6, max_y - min_y)
    cx = 0.5 * (wpos[used, 0].min() + wpos[used, 0].max())
    cz = 0.5 * (wpos[used, 2].min() + wpos[used, 2].max())
    wpos[:, 0] = (wpos[:, 0] - cx) * scale
    wpos[:, 1] = (wpos[:, 1] - min_y) * scale
    wpos[:, 2] = (wpos[:, 2] - cz) * scale

    # 4. Compute smooth welded vertex normals
    v0, v1, v2 = wpos[v_faces[:, 0]], wpos[v_faces[:, 1]], wpos[v_faces[:, 2]]
    fn = np.cross(v1 - v0, v2 - v0)
    wn = np.zeros_like(wpos)
    for k in range(3):
        np.add.at(wn, v_faces[:, k], fn)
    wn = np.divide(wn, np.maximum(np.linalg.norm(wn, axis=1, keepdims=True), 1e-8))

    # 5. Extract albedo texture & write OBJ / MTL / ZIP
    tex_filename = None
    if gltf.get("images"):
        img_meta = gltf["images"][0]
        ext = "jpg" if "jpeg" in img_meta.get("mimeType", "") else "png"
        tex_filename = f"{base_name}_albedo.{ext}"
        bv = gltf["bufferViews"][img_meta["bufferView"]]
        img_bytes = bin_data[bv.get("byteOffset", 0) : bv.get("byteOffset", 0) + bv["byteLength"]]
        with open(os.path.join(out_dir, tex_filename), "wb") as f:
            f.write(img_bytes)

    mtl_filename = f"{base_name}.mtl"
    obj_filename = f"{base_name}.obj"
    mtl_path = os.path.join(out_dir, mtl_filename)
    obj_path = os.path.join(out_dir, obj_filename)

    with open(mtl_path, "w", encoding="utf-8") as f:
        f.write(f"newmtl {base_name}_mat\n")
        f.write("Ka 1.000 1.000 1.000\n")
        f.write("Kd 1.000 1.000 1.000\n")
        f.write("Ks 0.100 0.100 0.100\n")
        f.write("d 1.0\n")
        f.write("illum 2\n")
        if tex_filename:
            f.write(f"map_Kd {tex_filename}\n")

    lines = [
        f"# Welded & internally boot-stitched from {os.path.basename(glb_path)} for Mixamo / Unity",
        f"mtllib {mtl_filename}",
        f"o {base_name}",
        f"usemtl {base_name}_mat",
    ]
    lines += [f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}" for p in wpos]
    lines += [f"vt {t[0]:.6f} {(1.0 - t[1]):.6f}" for t in raw_uv]
    lines += [f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}" for n in wn]
    for vf, uf in zip(v_faces, uv_faces):
        lines.append(
            f"f {vf[0]+1}/{uf[0]+1}/{vf[0]+1} {vf[1]+1}/{uf[1]+1}/{vf[1]+1} {vf[2]+1}/{uf[2]+1}/{vf[2]+1}"
        )

    with open(obj_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    zip_path = os.path.join(out_dir, f"{base_name}_for_mixamo.zip")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(obj_path, arcname=obj_filename)
        zf.write(mtl_path, arcname=mtl_filename)
        if tex_filename:
            zf.write(os.path.join(out_dir, tex_filename), arcname=tex_filename)

    comps_after = _get_components(v_faces, n_w)
    print(f"OK: {zip_path} ({os.path.getsize(zip_path):,} B) | components={[len(c) for c in comps_after]} | tris={len(v_faces):,} | height={target_height}m")


if __name__ == "__main__":
    if len(sys.argv) < 4:
        print("Usage: python glb_to_mixamo_zip.py <input.glb> <output_dir> <base_name> [target_height_m] [--flip-y]")
        sys.exit(1)
    h = float(sys.argv[4]) if len(sys.argv) >= 5 and not sys.argv[4].startswith("--") else 1.78
    flip = "--flip-y" in sys.argv
    convert_glb(sys.argv[1], sys.argv[2], sys.argv[3], h, flip_y_180=flip)
