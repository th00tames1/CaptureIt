# CaptureIt

[English](README.md)

화면 캡처와 주석 작성을 위한 데스크톱 도구입니다.
Windows 정식 에디션(.NET 8 + WPF)과 Linux/macOS 크로스 플랫폼 에디션(Avalonia)을 제공합니다.
Made by **Heechan Jeong** · MIT License

![Windows editor](docs/screenshot-editor.png)

## 다운로드 · 설치

**[⬇ 최신 버전 다운로드](https://github.com/th00tames1/CaptureIt/releases/latest)**

| OS | 파일 | 설치 방법 |
|---|---|---|
| **Windows 10/11** | [`CaptureIt-Setup-2.1.7-win-x64.exe`](https://github.com/th00tames1/CaptureIt/releases/download/v2.1.7/CaptureIt-Setup-2.1.7-win-x64.exe) | 설치 관리자를 실행합니다. 관리자 권한이 필요하지 않습니다. |
| Windows (무설치) | [`CaptureIt-Portable-2.1.7-win-x64.zip`](https://github.com/th00tames1/CaptureIt/releases/download/v2.1.7/CaptureIt-Portable-2.1.7-win-x64.zip) | 압축 풀고 exe 실행 |
| **Linux x64** | [`CaptureIt-2.1.7-linux-x64.tar.gz`](https://github.com/th00tames1/CaptureIt/releases/download/v2.1.7/CaptureIt-2.1.7-linux-x64.tar.gz) | 압축 해제 후 `./install.sh` |
| **macOS (Apple Silicon)** | [`CaptureIt-2.1.7-macos-apple-silicon.zip`](https://github.com/th00tames1/CaptureIt/releases/download/v2.1.7/CaptureIt-2.1.7-macos-apple-silicon.zip) | 압축 해제 → **우클릭 → 열기** |
| macOS (Intel) | [`CaptureIt-2.1.7-macos-intel.zip`](https://github.com/th00tames1/CaptureIt/releases/download/v2.1.7/CaptureIt-2.1.7-macos-intel.zip) | 압축 해제 → **우클릭 → 열기** |

> **Windows SmartScreen**: CaptureIt은 유료 코드 서명 인증서가 없는 신생 오픈소스 앱이라
> 처음 실행할 때 *"Windows에서 PC를 보호했습니다"* 경고가 뜰 수 있습니다.
> **추가 정보 → 실행**을 누르면 됩니다. 모든 릴리스 파일은 이 저장소의 공개 소스에서
> GitHub Actions가 자동 빌드하므로 [빌드 과정](.github/workflows/release.yml)을 직접 검증할 수 있고,
> 다운로드가 쌓이면 Microsoft 평판이 올라가 경고는 자연히 사라집니다.
>
> **macOS**: 서명되지 않은 앱이라 첫 실행은 우클릭 → 열기로 해야 하며, 화면 기록 권한을 허용해야 합니다.
> **Linux**: `gnome-screenshot` / `spectacle` / `grim`+`slurp` / `scrot` / ImageMagick 중 하나가 필요합니다
> (대부분의 배포판에 기본 포함). 클립보드 복사는 `wl-clipboard`(Wayland) 또는 `xclip`(X11).

## Windows 에디션 주요 기능

![Windows main toolbar](docs/screenshot-main.png)

### 캡처

| 기능 | 설명 | 기본 단축키 |
|------|------|------------|
| 영역 캡처 | 화면이 잠시 멈추고, 원하는 부분을 드래그로 선택 | `Ctrl+Shift+A` |
| 단위 영역 | 버튼·이미지 같은 화면 요소를 하이라이트해 클릭 한 번으로 | `Ctrl+Shift+E` |
| 창 캡처 | 마우스를 올린 창이 강조되고, 클릭 한 번으로 캡처 | `Ctrl+Shift+W` |
| 전체 캡처 | 모든 모니터를 한 번에. 모니터가 여러 대면 1번/2번/전체 중 선택 | `Ctrl+Shift+F` |
| 스크롤 캡처 | 창을 클릭하면 자동으로 끝까지 스크롤하며 한 장으로 이어붙임 | `Ctrl+Shift+S` |
| 고정 크기 | 화면에 고정된 틀. 원형 꼭지점 핸들로 대각선 조절하거나 숫자 입력 후 Enter/더블클릭 | `Ctrl+Shift+D` |
| 같은 영역 다시 캡처 | 직전 영역을 오버레이 없이 즉시 재캡처 | `Ctrl+Shift+R` |
| 지연 캡처 | 영역을 먼저 지정한 뒤 3초/5초/10초 카운트다운, 그 순간의 실제 화면 캡처 (열린 메뉴 캡처에 유용) | (앱 내부) |

> 모든 단축키는 설정에서 변경할 수 있습니다: 입력란을 클릭하고 원하는 조합을 누른 뒤
> 저장하면 즉시 적용됩니다 (Backspace로 해제). 기본 단축키가 다른 프로그램에 이미
> 사용 중이면 `Ctrl+Alt+…` → `Alt+Shift+…` 순으로 자동 대체 등록됩니다.

### 최근 캡처 목록

- 모든 캡처가 자동으로 목록에 쌓입니다 (최대 100장, 앱을 껐다 켜도 유지)
- 편집기 오른쪽 사이드바에서 썸네일로 보고, 클릭하면 언제든 다시 편집
- 편집 중 다른 항목으로 이동해도 그리던 내용이 자동 보존(해당 항목에 합성)
- 목록에서 바로 `Ctrl+C` → 어디든 `Ctrl+V` (채팅·문서에는 이미지로, 탐색기·메신저에는 파일로)
- 합성·자르기·저장으로 구워진 편집도 `Ctrl+Z`/`Ctrl+Y`로 단계별 되돌리기 (항목별 8단계)
- 항목별 우클릭: 복사 / 저장 폴더에 저장 / 삭제 · 하단: 모두 저장 / 모두 삭제

### 편집기

- 펜, 형광펜, 직선, 화살표, 사각형, 원, 텍스트, 번호 스탬프 ①②③
- 사각형/원 채우기·외곽선 색 지정 (체크무늬 투명 포함), 텍스트 글꼴·크기 선택
- 펜/형광펜 선택 시 현재 색이 반영된 도구 모양 커서
- 모자이크(개인정보 가리기) · 지우개 · 자르기 · 초기화
- 8가지 색상 + 4단계 굵기 · `Ctrl+휠` 확대/축소
- 저장(`Ctrl+S`) · 복사(`Ctrl+C`) · 다른 이름으로 저장 (PNG/JPG/BMP)

### 편의 기능

- 한국어/영어 전환 · Windows 시작 시 자동 실행 · 트레이 상주
- 캡처 시 자동 클립보드 복사(이미지+PNG+파일 동시) · 창 겹침 방지 배치
- 메인 툴바 항상 위 고정(핀) · 창 위치 기억

## 크로스 플랫폼 에디션 (Linux · macOS)

![Cross-platform edition](docs/screenshot-cross.png)

Avalonia 기반의 경량 에디션입니다. 캡처는 각 OS의 네이티브 스크린샷 도구
(macOS `screencapture`, Linux `gnome-screenshot`/`grim` 등)에 위임합니다.
Wayland, 멀티 모니터, 권한 처리를 OS가 가장 정확하게 다루기 때문입니다.

| 기능 | 지원 |
|---|---|
| 영역/전체 캡처 | ✅ (macOS·Linux는 OS 네이티브 영역 선택 UI) |
| 주석: 펜·사각형·화살표 | ✅ |
| 자르기 · 실행 취소 | ✅ |
| 캡처 목록 (100장) | ✅ |
| 클립보드 복사 · PNG 저장 | ✅ |
| 한국어/영어 | ✅ |
| 창/요소/스크롤/고정크기 캡처, 전역 단축키, 트레이 | Windows 에디션 전용 |

## 소스에서 빌드

```bash
# Windows 풀 에디션 (Windows에서만)
dotnet run --project CaptureIt

# 크로스 플랫폼 에디션 (어디서나)
dotnet run --project CaptureIt.Cross
```

릴리스 빌드는 [.github/workflows/release.yml](.github/workflows/release.yml)이 태그 푸시 시 자동 생성합니다.

## 알려진 제한

- Windows: 모니터별 배율(DPI)이 달라도 동작합니다 (Per-Monitor V2). 특수한 배치에서 좌표가 어긋나면 이슈로 알려주세요.
- 스크롤 캡처는 창의 스크롤바를 움직이는 방식이라, 휠을 자체 처리하는 페이지(일부 슬라이드·지도)는 깔끔하게 이어붙지 않을 수 있습니다.
- macOS/Linux 에디션은 CI에서 빌드되지만 상시 테스트 환경은 Windows입니다. 문제가 있으면 [이슈](https://github.com/th00tames1/CaptureIt/issues)로 알려주세요.

## 라이선스

MIT © 2026 [Heechan Jeong](https://github.com/th00tames1)
