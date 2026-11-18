# CANDesk

현재 릴리스 버전: **1.1.0**. 빌드 버전은 `Directory.Build.props`의 `Version`에서 관리합니다.

### 변경 이력 (Fixes & Changes)

#### 2026-11-18 — Vector CAN FD 및 장치 자동 검색

- **Vector CAN FD:** 지원 채널에서 ISO CAN FD 연결과 최대 64바이트 송수신, BRS, 확장 ID 및 하드웨어 수신 타임스탬프를 지원합니다. FD 연결에서도 Classic CAN 프레임을 송수신할 수 있습니다.
- **VN1630/VN1640 자동 검색:** Vector XL 드라이버가 제공하는 CAN 채널을 직접 검색하고 채널 이름과 시리얼 번호를 표시합니다. 기존 `xlCanGP` 애플리케이션 매핑에 등록된 채널만 검색하던 제한을 제거했습니다. VN1640A에서 CAN 채널 1~4 검색과 FD 지원 여부를 확인했습니다.
- **타이밍 설정 및 검증:** Nominal/Data 프리셋과 Raw 세그먼트 입력을 지원하며, Raw 미리보기도 Vector 제한에 맞춰 계산합니다. 잘못된 FD 플래그·페이로드 및 타이밍을 검사하고, 다른 애플리케이션이 채널을 사용하는 경우 활성 ISO CAN FD 설정이 요청한 설정과 일치하는지 확인합니다.

#### 1.1.0 (2026-10-29) — CAN 장치 선택

- **장치별 연결:** 연결 설정에 **DEVICE** 선택란을 추가했습니다. **VENDOR → DEVICE → CHANNEL → Connect** 순서로 선택해 같은 타입의 CAN 모듈이 여러 개 연결되어 있어도 원하는 장치에 연결할 수 있습니다. 채널 목록은 선택한 장치가 제공하는 채널로 갱신됩니다.
- **PEAK 장치 식별:** PCAN-USB/PCAN-PCI (PCI Express) 번호와 하드웨어 ID를 함께 표시합니다. 예: `PCAN-USB 1 (ID: 00000001)`.
- **CANable 장치 식별:** `WinUSB` 접두어, 장치 이름, Windows 장치 식별 정보와 USB 위치 정보를 표시합니다. DEVICE 선택란에 마우스를 올리면 전체 장치 ID를 확인할 수 있습니다. candleLight 펌웨어와 WinUSB 드라이버가 필요합니다.
- **자동 검색 및 새로고침:** 앱 시작과 제조사 변경 시 장치 목록을 자동 검색합니다. 장치를 추가·제거한 뒤 **Refresh**로 다시 검색할 수 있으며, 기존에 선택한 장치와 채널이 남아 있으면 선택을 유지합니다.
- **연결 대상 확인:** 연결 직전에 선택한 장치 ID와 채널을 다시 확인합니다. 선택한 장치가 사라졌거나 해당 채널을 찾을 수 없으면 다른 모듈로 대신 연결하지 않습니다. 검색 실패 시 드라이버 진단 정보를 표시하고, 연결 상태에는 실제로 연결한 장치와 채널을 표시합니다.

#### 2026-10-23 — TX 편집 및 송신 제어 개선

- **Fix — 시그널 값 적용 시점:** TX 시그널 값은 입력 후 **Enter**를 눌렀을 때 페이로드와 실행 중인 주기·트리거 송신에 반영됩니다. 입력 중이거나 포커스를 옮길 때는 적용되지 않으며, 숫자로 해석할 수 없는 입력은 기존 페이로드를 유지합니다.
- **Change — TX 메시지 삭제:** 각 TX 메시지에 **Delete** 버튼을 추가했습니다. 삭제 시 해당 메시지의 주기·트리거 송신을 모두 중지하고 목록에서 제거합니다.
- **Change — 시그널 범위 표시:** 값 입력란 오른쪽에 메시지 DB에 정의된 **Min / Max**를 표시합니다. 정의되지 않은 한계값은 `—`로 표시합니다.

### Build files with PowerShell (Windows x64)

Run this command from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The script automatically installs a missing .NET SDK and publishes a Release build
with the .NET and WPF runtimes to `build\Release\`. Run `build\Release\CANDesk.App.exe` directly.
To distribute the app, copy the entire `build\Release` folder; the destination PC does not
need the source code or a .NET SDK. CAN hardware vendor drivers are still required
for real hardware. No Start menu shortcut is created. Use `-WhatIf` to preview.

For Explorer/double-click use, run **build.cmd** instead. It runs PowerShell with
the process execution policy set to Bypass and waits for a key before closing.
Keep `build.ps1`, `install.ps1`, and the complete `src` tree together.

The build-only smoke check reuses the installer checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\Install.Tests.ps1 -BuildOnly
```

### Install with PowerShell (Windows x64)

Run this command from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

If a suitable .NET SDK is missing, the script automatically installs .NET SDK 8 to
`%LOCALAPPDATA%\Microsoft\dotnet` using Microsoft's official
[dotnet-install script](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script).
An existing .NET SDK 8 or newer is reused. A separate .NET Framework installation is
not required. The script restores NuGet dependencies, publishes a Release build with
the .NET runtime included, installs it to
`%LOCALAPPDATA%\Programs\CANDesk`, and creates a **CANDesk** Start menu shortcut.
Administrator rights are not required for the default installation. Internet access is
needed to download a missing SDK and restore NuGet packages and runtime packs that
are not already cached. The SDK installation does not change the permanent PATH.
Close CANDesk and run the same command to update it. A failed publish leaves the
existing installation untouched. User settings in `%APPDATA%\CANDesk` are preserved.

Use `-InstallDirectory 'D:\Apps\CANDesk'` to change the destination, or `-WhatIf` to
preview the action. CAN hardware vendor drivers must be installed separately;
see [vendor runtime notes](src/CANDesk.App/vendor/README.md) for optional DLLs.

For Explorer/double-click use, run **install.cmd** to keep the result visible.
Both scripts save a transcript to `%TEMP%\CANDesk-install-<id>.log` and print its
path. A failure includes the failing step; temporary build files are removed,
but the log remains. PowerShell execution blocked before the script starts cannot
create a log. Domain execution policies can still override the launcher's Bypass.

Common causes on a PC without development tools:

- An incomplete download or running from inside a ZIP: extract the entire repository first.
- Blocked HTTPS, proxy authentication, or untrusted certificates: allow access to
  `dot.net`, `aka.ms`, `builds.dotnet.microsoft.com`, and `api.nuget.org`, including
  their download/CDN redirects. The bootstrap download has a timeout and a second
  Microsoft endpoint; SDK installation allows the official fallback when `aka.ms` fails.
- An old or damaged .NET host on PATH: unusable hosts are skipped and a local .NET 8 SDK is installed.
- A running CANDesk, a read-only checkout/destination, or insufficient disk space:
  close the app and use a writable folder with room for the SDK, packages, and runtime files.

Visual Studio is not required. Both commands build from source and need Internet
access on their first run. To avoid SDK installation and build downloads on another
PC, distribute the complete prebuilt `build\Release` folder instead.

The installer smoke check runs without building the app or changing the Start menu:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\Install.Tests.ps1
```

**CANDesk** is an all-in-one, modular CAN bus workstation engineered for seamless testing, diagnostics, and packet manipulation across diverse hardware platforms.

Eliminate vendor lock-in with a unified interface: CANDesk integrates multi-vendor hardware abstraction, multi-format CAN message database support (DBC / custom XML / customer CSV), an in-app message database editor, and highly customizable transmission/reception workflows into a single workbench.

---

### Key Highlights
- **Multi-Vendor Hardware Abstraction:** Modular driver architecture supporting PEAK-System, Vector, Kvaser, and CANable (candleLight firmware) without code refactoring.
- **CAN ↔ CAN-FD, No Hassle:** Switch between Classic CAN and CAN-FD freely before connecting — no more "built it as CAN-FD when it should've been Classic" rework. Baudrate/Sample Point are chosen from PCAN-View-style preset tables by default, with manual entry always available.
  - Vector CAN channels, including VN1630/VN1640, are discovered directly from the XL driver with channel names and serial numbers. Capable channels support ISO CAN FD, up to 64 payload bytes, BRS, and independent nominal/data timing; application-channel mapping is not required.
- **Multi-Format Message Database:** Load and decode `.dbc`, an in-house `.xml` schema, and vendor-varying customer `.csv` files, all resolved into one common signal model.
- **Message Database Editor:** Edit loaded messages/signals directly in the GUI, including adding arbitrary new messages and signals — not just read-only viewing.
- **RX Monitor vs. Trace:** A PCAN-View-style Receive table where each CAN ID updates in a single row, separate from a timestamp-ordered Trace log that appends every frame — each with its own independent filter.
- **Customizable Transmit/Receive Engine:** Flexible payload builder supporting cyclic, triggered, and manual transmissions with real-time payload modification and E2E (rolling counter / CRC) support.
- **Trace & Monitoring:** High-resolution (microsecond, hardware-timestamped) frame logging, signal filtering, and real-time visualization with a Raw Hex ⇄ Msg/Signal display toggle.

------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
**CANDesk**는 다양한 하드웨어 인터페이스 환경에서 CAN 통신 테스트, 진단 및 패킷 조작을 단일 작업 환경으로 통합한 **모듈형 올인원 CAN 워크스테이션**입니다.

특정 제조사 전용 소프트웨어의 종속성에서 벗어나, 멀티 벤더 하드웨어 추상화 계층(HAL)을 기반으로 멀티포맷 메시지 DB(DBC/XML/CSV) 파싱·편집 및 유연한 송수신 제어 워크플로우를 제공합니다.

---

### 주요 기능 (Key Features)

* **멀티 벤더 하드웨어 추상화 (Hardware Abstraction Layer)**
  * 제조사별 전용 툴에 얽매이지 않고 플러그인 방식으로 하드웨어 드라이버 교체 지원
  * 기본 지원 인터페이스: PEAK-System, Vector, Kvaser, CANable(candleLight 펌웨어)
  * 단일 API를 통한 다채널/멀티 인터페이스 동시 제어

* **CAN ↔ CAN-FD 자유 전환 및 Baudrate 설정**
  * 연결 직전까지 Classic CAN ↔ CAN-FD 모드를 자유롭게 전환 — "만들고 보니 FD로 잡아야 해서 다시 손보는" 불편 제거
  * PCAN-View 스타일의 표준 Baudrate/Sample Point 프리셋 테이블에서 선택하는 것을 기본으로 하되, 필요 시 값을 직접 입력 가능
  * CAN-FD 선택 시 Arbitration(Nominal)과 Data Phase 비트레이트를 별도로 구성
  * Vector 지원 채널에서 ISO CAN FD 최대 64바이트 송수신, BRS 및 확장 ID 지원

* **멀티포맷 메시지 DB 파싱 및 편집**
  * `.dbc`(표준), `.xml`(팀 자체 정의 포맷), `.csv`(고객사별 다양한 양식) 로드 및 공통 신호 모델로 자동 수렴
  * Raw 바이트 데이터와 물리값(Physical Value, Engineering Units) 간 양방향 실시간 변환
  * GUI 내 메시지 DB 편집기 제공 — 기존 메시지/시그널 수정은 물론 **임의의 신규 메시지·시그널 추가**도 지원

* **RX 확인(Monitor)과 Trace의 분리**
  * **RX 확인**: 동일 CAN ID는 하나의 행(Row)에서 최신값·수신 카운트·주기를 계속 갱신 (PCAN-View Receive 방식)
  * **Trace**: 모든 프레임을 타임스탬프 순으로 매번 새 행에 반복 기록하는 append-only 로그
  * 두 화면 모두 서로 독립적인 필터(ID/Mask/Rx·Tx·Error)를 보유
  * Raw Hex ⇄ Msg/Signal 디코딩 뷰를 즉시 전환 가능

* **자유도 높은 패킷 송수신 엔진**
  * 주기 송신(Cyclic), 단발 송신(Single/Triggered), 조건부 응답 시나리오 설정
  * 실시간 페이로드 편집 및 체크섬(CRC)/카운터(Rolling Counter) 등 E2E 커스텀 계산 로직 반영 가능
  * 필터링(ID, Mask)을 통한 목적 프레임 선별 수신

* **고성능 트레이스 및 로깅**
  * 마이크로초 단위 하드웨어 타임스탬프 기반 실시간 프레임 트레이스
  * 대용량 패킷 버퍼링 및 CSV, BLF, ASC 포맷 내보내기/불러오기 지원

---

### Vector 연결 (Classic CAN / CAN FD)

Vector 하드웨어 드라이버와 64비트 XL Driver Library가 필요합니다. `vxlapi64.dll` 배치 방법은 [벤더 런타임 안내](src/CANDesk.App/vendor/README.md)를 참고하세요.

1. Vector 장치를 연결하고 **VENDOR → Vector**를 선택합니다. 연결 후 목록을 갱신하려면 **Refresh**를 누릅니다.
2. **DEVICE**에서 채널 이름과 시리얼 번호를 확인하고 **CHANNEL**을 선택합니다. 예: `VN1640A Channel 1 (S/N 123456)`.
3. **Classic CAN** 또는 **CAN FD**를 선택하고 Nominal 비트레이트와 Sample Point를 설정합니다. CAN FD에서는 Data Phase 타이밍도 설정합니다.
4. **Connect**로 연결합니다. 송신할 프레임의 **FD** 및 **BRS** 옵션을 필요한 형식에 맞게 선택합니다.

CAN 채널은 XL 드라이버에서 직접 검색하므로 Vector Hardware Config의 `xlCanGP` 등 애플리케이션 채널에 별도로 매핑할 필요가 없습니다. 드라이버가 제공하는 Virtual CAN 채널도 목록에 표시될 수 있습니다. FD 지원 여부는 각 채널의 드라이버 기능 정보로 확인하며, 지원하지 않는 채널의 FD 연결은 오류로 알립니다.

Vector CAN FD 타이밍은 **80 MHz** 기준입니다. 프리셋 또는 Raw 세그먼트를 사용할 수 있으며, Data 비트레이트는 Nominal 이상이면서 최소 **25 kbps**여야 합니다. Raw 입력 범위는 다음과 같습니다.

| 항목 | Nominal | Data |
| --- | --- | --- |
| TSeg1 / TSeg2 | 2~254 | 2~126 |
| SJW | 1~128, TSeg2 이하 | 1~64, TSeg2 이하 |

BRP는 양의 정수이며, 입력한 BRP와 세그먼트에서 비트레이트를 계산합니다. FD 프레임에는 RTR을 사용할 수 없으며, BRS는 FD 프레임에서만 사용할 수 있습니다. 8바이트를 초과하는 페이로드는 FD 프레임으로 보내야 합니다. FD DLC에 맞지 않는 길이는 다음 전송 길이까지 0으로 패딩됩니다.

다른 애플리케이션이 채널의 초기화 권한을 보유한 경우, 활성 ISO CAN FD 모드·Nominal/Data 비트레이트·Sample Point가 요청 설정과 맞아야 연결됩니다. 일치하지 않으면 해당 애플리케이션의 설정에 맞추거나 채널을 해제한 뒤 다시 연결합니다.

장치가 표시되지 않으면 Vector Hardware Manager에서 인식 상태를 확인하고 **Refresh**를 누릅니다. 이전 CANDesk 빌드를 실행 중이라면 앱을 종료한 뒤 최신 빌드 또는 설치 스크립트로 업데이트하세요. 검색 실패 시 표시되는 XL 드라이버 진단 정보도 확인할 수 있습니다.

현재 실장비 확인 범위는 **VN1640A CAN 채널 1~4 검색 및 FD 지원 정보 조회**입니다. 실제 버스에서의 CAN FD 송수신 검증은 별도로 필요합니다.

---

### 아키텍처 개요 (Architecture)

```text
+------------------------------------------------------------------------+
|                              CANDesk GUI                                |
|  [Message Monitor]  [Trace]  [DBC Signal Tree]  [Message DB Editor]     |
|  [Transmit Panel]   [Signal Plot]                                       |
+------------------------------------------------------------------------+
                                    │
+------------------------------------------------------------------------+
|                              Core Engine                                 |
|   • Message DB Parser/Writer (DBC / XML / CSV) & Decoder                |
|   • TX Scheduler (Cyclic / Triggered / Manual, E2E CRC/Counter)         |
|   • RX Dispatcher & Filter Engine (Message Monitor / Trace 분리 소비)    |
+------------------------------------------------------------------------+
                                    │
+------------------------------------------------------------------------+
|              Hardware Abstraction Layer (ICanDevice)                    |
|     [PEAK PCAN]   [Kvaser CANlib]   [Vector XL]   [CANable/Candlelight] |
+------------------------------------------------------------------------+
```

상세 아키텍처 설계 및 인터페이스 정의는 `_brain/prj.CANDesk/Project_Architecture.md`에서 관리됩니다.
