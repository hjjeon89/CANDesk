# CANDesk

**CANDesk** is an all-in-one, modular CAN bus workstation engineered for seamless testing, diagnostics, and packet manipulation across diverse hardware platforms.

Eliminate vendor lock-in with a unified interface: CANDesk integrates multi-vendor hardware abstraction, multi-format CAN message database support (DBC / custom XML / customer CSV), an in-app message database editor, and highly customizable transmission/reception workflows into a single workbench.

---

### Key Highlights
- **Multi-Vendor Hardware Abstraction:** Modular driver architecture supporting PEAK-System, Vector, Kvaser, and CANable (candleLight firmware) without code refactoring.
- **CAN ↔ CAN-FD, No Hassle:** Switch between Classic CAN and CAN-FD freely before connecting — no more "built it as CAN-FD when it should've been Classic" rework. Baudrate/Sample Point are chosen from PCAN-View-style preset tables by default, with manual entry always available.
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
