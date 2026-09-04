# CANDesk

**CANDesk** is an all-in-one, modular CAN bus workstation engineered for seamless testing, diagnostics, and packet manipulation across diverse hardware platforms. 

Eliminate vendor lock-in with a unified interface: CANDesk integrates multi-vendor hardware abstraction, comprehensive CAN DB (DBC) parsing, and highly customizable transmission/reception workflows into a single workbench.

---

### Key Highlights
- **Multi-Vendor Hardware Abstraction:** Modular driver architecture supporting major interfaces (Vector, PEAK-System, Kvaser, ValueCAN, etc.) without code refactoring.
- **CAN DB (DBC) Integration:** Load and decode message definitions directly to view and manipulate signals in engineering units.
- **Customizable Transmit/Receive Engine:** Flexible payload builder supporting cyclic, triggered, and manual transmissions with real-time payload modification.
- **Trace & Monitoring:** High-resolution frame logging, signal filtering, and real-time visualization.

------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
**CANDesk**는 다양한 하드웨어 인터페이스 환경에서 CAN 통신 테스트, 진단 및 패킷 조작을 단일 작업 환경으로 통합한 **모듈형 올인원 CAN 워크스테이션**입니다.

특정 제조사 전용 소프트웨어의 종속성에서 벗어나, 멀티 벤더 하드웨어 추상화 계층(HAL)을 기반으로 CAN DB(DBC) 파싱 및 유연한 송수신 제어 워크플로우를 제공합니다.

---

### 주요 기능 (Key Features)

* **멀티 벤더 하드웨어 추상화 (Hardware Abstraction Layer)**
  * 제조사별 전용 툴에 얽매이지 않고 플러그인 방식으로 하드웨어 드라이버 교체 지원
  * 주요 제조사 인터페이스 연동 대응 (PEAK, Kvaser, Vector, Intrepid ValueCAN, 커스텀 시리얼/USB 등)
  * 단일 API를 통한 다채널/멀티 인터페이스 동시 제어

* **CAN DB (DBC) 파싱 및 신호 디코딩**
  * `.dbc` 파일 로드 및 메시지/시그널 트리 구조 자동 파싱
  * Raw 바이트 데이터와 물리값(Physical Value, Engineering Units) 간 양방향 실시간 변환
  * 메시지 단위 및 개별 시그널 단위 모니터링/조작 지원

* **자유도 높은 패킷 송수신 엔진**
  * 주기 송신(Cyclic), 단발 송신(Single/Triggered), 조건부 응답 시나리오 설정
  * 실시간 페이로드 편집 및 체크섬/카운터 커스텀 계산 로직 반영 가능
  * 필터링(ID, Mask)을 통한 목적 프레임 선별 수신

* **고성능 트레이스 및 로깅**
  * 마이크로초 단위 타임스탬프 기반 실시간 프레임 트레이스
  * 대용량 패킷 버퍼링 및 CSV, BLF, ASC 포맷 내보내기/불러오기 지원

---

### 아키텍처 개요 (Architecture)

```text
+--------------------------------------------------------+
|                     CANDesk GUI                        |
|  [Trace Monitor]  [DBC Signal View]  [Transmit Panel]  |
+--------------------------------------------------------+
                           │
+--------------------------------------------------------+
|                      Core Engine                       |
|   • DBC Parser & Decoder (Raw <-> Physical Value)      |
|   • TX Scheduler (Cyclic / Manual / Scripted)          |
|   • RX Dispatcher & Filter Engine                      |
+--------------------------------------------------------+
                           │
+--------------------------------------------------------+
|         Hardware Abstraction Layer (ICanDevice)        |
|  [PEAK PCAN]  [Kvaser CANlib]  [Vector XL]  [Others]   |
+--------------------------------------------------------+
