# 어사검 (eosa_blade)

한국풍 2.5D 1:1 패링 액션 (Unity 6000.3.25f1 URP, Android 가로). 기획: `C:\project\docs\2_5d_gdd.md`, `2_5d.md`

빌드: `Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod EosaBlade.Editor.BuildScript.AndroidLite` → `Builds/eosa_blade_v<버전>_lite.apk`

## 변경 내역
- v0.2.5 — 칼 막기·패링 자세 직접 구현(방패 모션 대체), 공격 연타 변형, 엉덩이 흔들림 고정
- v0.2.4 — 캐릭터 겹침 수정(최소 간격 2.25, 돌진 거리 제한)
- v0.2.3 — 찌르기 공중부양·왼쪽 빈칸·인살 놓침 체력 회복 수정, 2페이즈 회전베기·충격파
- v0.2.2 — 캐릭터 크기 정상화(FBX 배율 100)·마당 바닥 텍스처
- v0.2.1 — 모바일 최적화(HDR 끔·렌더 0.85·ASTC·ADPCM·HUD GC 0)
- v0.2.0 — 암행어사·대장군 3D 모델과 Mixamo 모션 11종, 무기·검기, 산사 배경, 아트 HUD
- v0.1.0 — 그레이박스 패링 코어·대장군 임시 AI·터치 HUD·CC0 효과음
