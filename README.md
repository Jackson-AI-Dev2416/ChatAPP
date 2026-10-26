# TS3 Messenger (Windows)

사내 LAN용 **Windows 데스크톱 채팅** 클라이언트입니다. TS-3 백엔드(`chat`, `file-transfers`, WebSocket)와 연동합니다.

## 요구 사항

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (빌드 시)
- TS-3 백엔드 HTTPS/WSS (사내 CA 인증서 신뢰 설정)

## 보안 요약

- **전송**: TLS(HTTPS/WSS) 필수. 클라이언트는 `DangerousAcceptAnyServerCertificate`를 사용하지 않습니다.
- **채팅 문구**: 서버 DB에 **평문** 저장(TLS 전송). 관리자 감독·여러 PC에서 동일 이력 조회. 이모지는 Emoji.Wpf(컬러) 사용.
- **파일**: 사용자 요청에 따라 **암호화하지 않음**. LAN TCP P2P 전송, 서버에는 이름·크기·상태 메타만 저장.

자세한 내용: [docs/SECURITY.md](./docs/SECURITY.md)

## 실행·빌드

```powershell
cd chattingapp
.\scripts\build.ps1              # Debug
.\scripts\build.ps1 -Release     # Release exe (self-contained)
```

출력: `dist\Ts3Messenger.exe`

**대화 삭제**는 백엔드 `POST /api/v1/chat/rooms/{id}/clear-client-history` 및 DB 마이그레이션 `20260320_0019`이 필요합니다. 404가 나면 `docker compose build backend` 후 `alembic upgrade head`를 실행하세요.

오류 상세 로그: `%AppData%\Ts3Messenger\logs\messenger-YYYY-MM-DD.log` (화면에는 한국어 요약만 표시)

## 설정

첫 실행 시 로그인 화면에서 **서버 URL**을 입력합니다.

- LAN HTTP(기본값): `http://192.168.0.10` (서버 `docker/.env`의 `TS3_LAN_HOST`와 맞출 것)
- 로컬 HTTPS: `https://127.0.0.1:8443` (또는 `docker/.env`의 `TS3_LOCAL_HTTPS_PORT`)

**사용자 정보 보관** 체크 시 ID·비밀번호·URL이 PC에 저장됩니다. **Windows 시작 시 자동 실행**(`--startup`)일 때만 로그인 창 없이 트레이에만 머무릅니다. exe를 직접 실행하면 로그인 성공 시 채팅 창이 보입니다. 채팅 창이 열린 상태에서 **로그아웃**하거나 **서버가 끊기면** 로그인 창이 열립니다. 서버 끊김으로 열린 로그인 창은 **10초마다** 자동 재로그인을 시도하고, 성공하면 채팅 창으로 돌아갑니다. 창이 트레이에만 있을 때 서버가 끊기면 로그인 창은 띄우지 않고, 같은 간격으로 조용히 다시 접속합니다.  
`%AppData%\Ts3Messenger\settings.json`에 저장됩니다.

## 백그라운드·자동 실행

- **창 닫기(X)**: 앱이 종료되지 않고 **작업 표시줄 알림 영역(트레이)** 에 남습니다. 더블 클릭 또는 메뉴 **열기**로 다시 표시합니다.
- **완전 종료**: 트레이 아이콘 우클릭 → **종료**.
- **새 메시지 알림**: 창을 닫았거나 최소화·다른 대화를 보는 중이면 트레이 **말풍선 알림**과 짧은 알림음이 납니다. 말풍선을 클릭하면 해당 대화로 열립니다.
- **Windows 시작 프로그램**: 한 번 실행하면 현재 사용자 레지스트리 `Run`에 `Ts3Messenger`가 등록되어, **PC 재부팅 후에도 자동 실행**됩니다(세션 쿠키가 있으면 로그인 화면 없이 채팅 화면).
- **단일 인스턴스**: exe를 다시 실행하면 새 프로세스는 뜨지 않고, 이미 실행 중인 창(또는 트레이)만 앞으로 가져옵니다.

## 기능

- 주소록(온라인·최근 대화 순), 미읽음 배지(작업 표시줄·목록)  
  서버 `GET /api/v1/chat/contacts`가 내려주는 목록만 표시합니다. **미로그인·CLI 초기 총관리자(직원 프로필 없음)·채팅 공개키 없음** 계정은 서버에서 제외됩니다 (`backend/app/modules/chat/service.py` → `_visible_in_chat_directory`). **백엔드 재배포 후** 메신저를 다시 빌드·실행해야 반영됩니다.
- 실시간 채팅(WebSocket), 전송·전달·읽음 표시
- 이모지 입력, 파일 전송(원형 %·MB 진행률): **파일 여러 개**·**폴더**(ZIP으로 전송)·**드래그 앤 드롭** 지원. 폴더는 `{이름}.zip`으로 받습니다.
- 그룹 채팅(방 키 봉투)
