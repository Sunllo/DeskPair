# Sunllo DeskPair

[English](../../README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md) ·
[日本語](README.ja.md) · **한국어** · [Deutsch](README.de.md) ·
[Français](README.fr.md) · [Español](README.es.md) ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> 이 문서는 [README.md](../../README.md)의 번역입니다. 내용이 다를 때는 영어판을 기준으로 합니다.

C# / .NET 10으로 작성한 크로스 플랫폼(Windows／macOS／Linux) 원격 데스크톱 시스템입니다.

- **데스크톱 앱**(Avalonia) — 제어하는 쪽과 제어받는 쪽이 하나의 앱에 들어 있습니다.
- **휴대폰 앱**(Kotlin Multiplatform, Android와 iOS) — 휴대폰에서 제어하는 쪽입니다.
- **Rendezvous** — ID 등록, 접속 상태, NAT 홀 펀칭 시그널링.
- **Relay** — 직접 연결할 수 없는 두 기기 사이의 TCP를 중계합니다.

계정은 선택 사항입니다. 컴퓨터나 휴대폰을 한 사람에게 연결하고, 저장한 컴퓨터 목록을 기기마다 똑같이
유지합니다. 계정은 `deskpair.app`에서 운영하는 서비스이며 앱은 HTTPS(`/api/v1`)로 접속합니다. 이 서비스의
코드는 공개하지 않습니다.

구조는 [RustDesk](https://github.com/rustdesk/rustdesk)를 참고했지만 자체 프로토콜(protobuf 메시지,
종단 간 AES-256-GCM, ECDSA P-256 신원)을 사용합니다. 자세한 내용은 `docs/architecture.md`를 보십시오.
`docs/` 아래의 문서는 번체 중국어로 작성되어 있습니다.

## 스크린샷

Windows용 데스크톱 앱입니다. 이미지는 `tools/DeskPair.Tools.Screenshots`가 앱 자체의 창을 그려서 만든 것이며,
이미지 속 컴퓨터, 사람, 주소는 모두 가상입니다.

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/remote-session.png" width="100%" alt="원격 세션"><br>
      다른 컴퓨터를 제어하는 화면. 연결된 컴퓨터마다 탭이 따로 있습니다.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/file-transfer.png" width="100%" alt="파일 전송"><br>
      파일 전송. 왼쪽이 이 컴퓨터, 오른쪽이 상대 컴퓨터입니다.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/home.png" width="100%" alt="기본 창"><br>
      이 컴퓨터의 ID와 일회용 비밀번호, 최근에 연결한 컴퓨터.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/devices.png" width="100%" alt="기기"><br>
      그룹으로 정리해 저장한 기기와 온라인 여부.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/terminal.png" width="100%" alt="원격 터미널"><br>
      다른 컴퓨터의 셸. 위쪽에 표시된 계정으로 실행됩니다.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/incoming-request.png" width="100%" alt="연결 요청"><br>
      누군가 연결을 요청하면 이 컴퓨터 앞의 사람이 수락하거나 거부하고, 허용할 권한을 고릅니다.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/history.png" width="100%" alt="연결 기록"><br>
      이 컴퓨터에 누가 접속했고 무엇이 허용되었는지.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/settings-security.png" width="100%" alt="보안 설정"><br>
      보안 설정. 누가 연결할 수 있는지, 어떻게 자신을 증명하는지.
    </td>
  </tr>
</table>

## 다운로드

모든 릴리스는 [릴리스 페이지](https://github.com/Sunllo/DeskPair/releases/latest)와
[deskpair.app/download](https://deskpair.app/download)에 있으며, 각 파일의 SHA-256(`SHA256SUMS`)도 함께 제공합니다.

| 시스템 | 파일 |
|---|---|
| Windows 10 1809 이상 | `DeskPair-<version>-win-x64.zip`, `-win-arm64.zip`, `-win-x86.zip`(32비트). 아직 코드 서명을 하지 않아 처음 실행할 때 SmartScreen이 확인을 요청합니다. |
| macOS 13 이상 | `DeskPair-<version>-arm64.dmg`(Apple 실리콘), `-x86_64.dmg`(Intel). 서명되었고 Apple의 공증을 받았습니다. |
| Linux, glibc 2.31 이상 | x64, ARM64, 32비트 ARM(ARMv7)마다 `.deb`, `.rpm`, Arch 패키지, `.tar.gz`가 있습니다. |

Linux에서는 배포판의 기본 도구로 해당 패키지를 설치합니다(아래는 x64 파일 이름입니다).

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

패키지는 DeskPair를 `/usr/lib/deskpair`에 설치하며, 명령줄에서 `deskpair`로 실행할 수 있고 응용 프로그램
메뉴에도 나타납니다. 새 버전이 나오면 앱이 알려 주므로 같은 방법으로 새 패키지를 설치하십시오. `.tar.gz`는
어느 배포판에서나 압축을 푼 곳에서 실행되며, Windows와 macOS 버전처럼 스스로 업데이트합니다.

컴퓨터를 제어하는 휴대폰 앱은 아직 스토어에 출시되지 않았습니다.

## 빌드

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

.NET 10 SDK(`global.json`)가 필요합니다. 영상은 운영 체제의 인코더(Windows에서는 Media Foundation)를
사용합니다. 이 저장소에는 소프트웨어 H.264 코덱이 포함되어 있지 않습니다. H.264 특허는 OpenH264의 BSD-2
소스와는 별개이기 때문입니다. 다만 직접 제공할 수는 있습니다. `native/openh264/README.md`를 보십시오.

## 호스트와 데스크톱 앱

DeskPair는 하나의 실행 파일입니다. 실행하면 내 ID와 일회용 비밀번호가 표시되고, 다른 컴퓨터에 연결할 수
있으며, 같은 프로세스에서 호스트 엔진도 실행됩니다. 즉, 앱을 열어 두는 동안 이 컴퓨터는 제어를 받을 수 있고,
앱을 닫으면 그것이 멈춥니다.

```
# 앱
dotnet run --project src/DeskPair.Desktop

# 창 없이 엔진만: 데스크톱 세션이 없는 컴퓨터용
dotnet run --project src/DeskPair.Desktop -- --server
```

**무인 접속**(컴퓨터가 잠겨 있거나 아무도 로그인하지 않았을 때에도 접속할 수 있는 것)은 "설정 › 보안 ›
잠긴 상태에서도 접속 가능"에서 설치하는 역할입니다. Windows에서는 서비스, macOS에서는 launchd 에이전트,
Linux에서는 root 데몬이 됩니다. `docs/unattended-windows.md`와 `docs/unattended-linux.md`를 보십시오.
설치하지 않은 경우 "설정 › 일반 › **로그인하면 시작**"을 켜면 다시 시작하고 로그인한 뒤에 접속할 수 있습니다.

macOS에서는 앱을 `.app` 번들에서 실행하십시오. 화면 기록과 손쉬운 사용 권한은 번들에 부여되며, 터미널에서
실행한 파일에는 터미널의 권한이 적용됩니다. 그래서 macOS에서 `--server`는 테스트용 역할입니다.

직접 운영하는 서버를 쓰려면 "설정"(또는 `config.json`)에 rendezvous 서버와 그 공개 키를 입력하십시오.
`host:21118`로 직접 연결할 때는 서버가 필요 없습니다.

## 빠른 시작(창 없는 명령줄)

```
# 1. 서버(기본값: rendezvous udp+tcp/21116, nat-test 21115, http 21114, relay tcp/21117)
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. 클라이언트가 신뢰해야 하는 서버 키 읽기
curl http://127.0.0.1:21114/key

# 3. 호스트(9자리 ID와 임시 비밀번호를 출력)
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. 제어하는 쪽, 다른 터미널에서 실행. 입력한 줄은 채팅으로 전송됨
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

ID로 연결하면 LAN 주소(공인 IP가 같을 때), TCP 홀 펀칭, 마지막으로 relay 순서로 시도합니다.
`--force-relay`(PeerCli)／`ForceRelay`를 쓰면 직접 연결을 건너뜁니다. `docs/nat-test-matrix.md`를 보십시오.

Docker: `docker compose -f deploy/docker-compose.yml up --build`(호스트 네트워크 사용, 주의 사항은 파일 안에
있습니다). 실제로 서버를 운영하려면 `deploy/README.md`를 보십시오.

## 라이선스

DeskPair는 [GNU Affero 일반 공중 사용 허가서 3판](../../LICENSE)(`AGPL-3.0-only`)에 따른 자유 소프트웨어이며,
이 저장소의 앱과 서버 모두에 똑같이 적용됩니다. 다른 사람을 위해 서버를 운영한다면 제13조가 중요합니다.
그 사람들에게 실제로 운영 중인 프로그램의 소스 코드를, 수정한 부분까지 포함해 제공해야 합니다.

서드파티 구성 요소는 각자의 라이선스를 따릅니다. 앱의 "설정 › DeskPair 정보"에 목록이 있습니다.
