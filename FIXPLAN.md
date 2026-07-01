# # BGPLite — Обновлённый план исправлений

## Общий обзор

- **Всего пунктов:** 40
- **Полностью реализовано:** 7 (18 %)
- **Частично реализовано:** 1 (2 %)
- **Не реализовано:** 32 (80 %)

Этот план переупорядочивает задачи в соответствии с проведённым аудитом и приоритизирует их для дальнейшей разработки. Статусы и file:line сверены с актуальным `main` (аудит 2026-07-02).

---

## Приоритет 1 – Критические ошибки сессий и потокобезопасность

> **Актуализировано** под состояние `main` (после PR #5, #18–#21): 4 из 5 пунктов закрыты, hold-timer остаётся частичным.

| # | Описание | Статус | Файл / Строки | Действие | Уровень исправления |
|---|-----------|--------|---------------|----------|---------------------|
| 1.1 | Гонка при замене сессий | **Реализовано** | `BGPLite.Server/BgpServer.cs` – `:187-221` (TryAdd + TryUpdate CAS), `:263-271` (atomic remove) | Закрыто: `c58dc8b` (PR #5); ключ сессии `SessionKey` (IP+порт) — PR #20 / #18. | – |
| 1.2 | Параллельная запись в `_stream` | **Реализовано** | `BGPLite.Server/BgpSession.cs` – `:286` (`_sendLock`), `:848-882` (`SendMessageAsync`) | Закрыто: `bd924df` (PR #5). | – |
| 1.3 | Hold Timer | **Частично реализовано** | `BGPLite.Server/BgpSession.cs` – `:301, 326, 351-377, 362-369` (HoldTimer на `Interlocked` `_lastReceivedTicks`) | Таймер работает; перенос логики в класс `BgpTimers` через DI не выполнен (см. P6.1 / issue #10). | Архитектурный |
| 1.4 | NOTIFICATION при штатном завершении | **Реализовано** | `BGPLite.Server/BgpSession.cs` – `:254-258, 270-274` (Cease CAS), `:977-996` (`NotifyCeaseAsync`), `:345, 366, 1013` (teardown CAS) | Закрыто: Cease feat `037096e` + teardown races `7ba07c7`, `968f2e4`, `5387dd3` (PR #5). | – |
| 1.5 | `_state` без барьеров памяти | **Реализовано** | `BGPLite.Server/BgpSession.cs` – `:34` (`volatile BgpFsmState _state`), `:58` (`IsEstablished`) | Закрыто: `bd924df` (PR #5); дополнительно `Interlocked` для `_teardownReason`, `_lastReceivedTicks`, `_disposed`. | – |

---

## Приоритет 2 – Протокольные ошибки

| # | Описание | Статус | Файл / Строки | Действие | Уровень |
|---|-----------|--------|---------------|----------|----------|
| 2.1 | Порядок path attributes | **Отсутствует** | `BGPLite.Protocol/BgpMessageWriter.cs` – `:130-134` | Добавить сортировку атрибутов согласно RFC 4271. | Общий |
| 2.2 | Валидация `PrefixCodec` на `length > 32` | **Отсутствует** | `BGPLite.Protocol/PrefixCodec.cs` – `:21` (Encode), `:35, 37` (Decode) | Вставить проверку и бросить `ArgumentOutOfRangeException` (также в `EncodeList`/`DecodeList`). | Локальный |
| 2.3 | Усечение в OPEN-кодировании | **Отсутствует** | `BGPLite.Protocol/BgpMessageWriter.cs` – `:57, 88, 93` | Проверять, что длина `optParamsLen` и `capDataLen` ≤ 255, либо использовать 2-байтовое поле. | Локальный |
| 2.4 | `ReadAsPath` смешивает ASN размеры | **Реализовано** | `BGPLite.Protocol/AttributeHelper.cs` – `:22-50` (guard `:35-36`) | Закрыто: `9709c69` (guard `if (offset + segBytes > attr.Data.Length) break;`). | – |
| 2.5 | Валидация атрибутов при чтении | **Отсутствует** | `BGPLite.Protocol/AttributeHelper.cs` – `:7-10` (`ReadOrigin`), `:82-85` (`ReadNextHop`), `:99-106` (`ReadCommunities`); `BgpMessageReader.cs:192-193` | Добавить проверки длины данных и бросать `BgpParseException`. | Локальный |
| 2.6 | Валидация reserved attribute flag bits | **Отсутствует** | `BGPLite.Protocol/BgpMessageReader.cs` – `:174-196` (`ParseAttribute`) | Проверять, что бит 0x08 = 0 (в reader, не в `PathAttribute.cs`). | Локальный |
| 2.7 | Hold time high byte | **Отсутствует** | `BGPLite.Protocol/BgpMessageReader.cs` – `:63` | Проверять, что старший байт = 0; иначе NOTIFICATION (UnacceptableHoldTime, `BgpConstants.cs:39`). | Локальный |
| 2.8 | Валидация OPEN payload length | **Отсутствует** | `BGPLite.Protocol/BgpMessageReader.cs` – `:55-69` | Проверять `payload.Length == 10 + optParamsLen` (сейчас только `>=`, нет точного `!=`). | Локальный |
| 2.9 | `BgpConstants.IPAddressToUint` без IPv4 guard | **Отсутствует** | `BGPLite.Protocol/BgpConstants.cs` – `:94-98` | Проверять `AddressFamily.InterNetwork`. | Локальный |

---

## Приоритет 3 – Маршрутизация

| # | Описание | Статус | Файл / Строки | Действие | Уровень |
|---|-----------|--------|---------------|----------|----------|
| 3.1 | Утечка маршрутов через community-фильтр | **Отсутствует** | `BGPLite.Routing/PeerCommunityFilter.cs` – `:22-23` | Возвратить `false` когда фильтр включён и маршрут без community. | Локальный |
| 3.2 | Мутабельные массивы в `Route` | **Отсутствует** | `BGPLite.Routing/Route.cs` – `:8-9` | Перейти на `IReadOnlyList<uint>` или копировать массивы в конструкторе. | Общий |
| 3.3 | `RouteTable.AddOrUpdate` ненадёжен | **Отсутствует** | `BGPLite.Routing/RouteTable.cs` – `:11-18` | Заменить на `TryAdd` + lock или `GetOrAdd` с проверкой. | Локальный |
| 3.4 | Валидация prefix length и masking | **Отсутствует** | `BGPLite.Routing/Route.cs` – `:5-6`, `RouteTable.cs:14` | Применять маскирование (есть только в `ExactUnionPrefixAggregator`, нет в `Route`) и проверку диапазона. | Локальный |
| 3.5 | Longest-prefix-match | **Отсутствует** | `BGPLite.Routing/RouteTable.cs` (класс) | Добавить метод `Lookup(uint address)` в `RouteTable` (может переиспользовать `Enumerate()`). | Архитектурный |

---

## Приоритет 4 – Конфигурация

| # | Описание | Статус | Файл / Строки | Действие | Уровень |
|---|-----------|--------|---------------|----------|----------|
| 4.1 | Валидация RouterId | **Отсутствует** | `BgpConfig.cs` – строка 12 (default `0.0.0.0`) | Сделать поле обязательным и проверять ≠ `0.0.0.0` при загрузке. | Локальный |
| 4.2 | Валидация HoldTime/KeepAlive | **Отсутствует** | `BgpConfig.cs` – `:14-18` | Проверять `HoldTime` ≥ 3 (если ≠ 0) и `KeepAlive` ≥ 1, а также `KeepAlive ≤ HoldTime/3`. | Локальный |
| 4.3 | Валидация ApiPort | **Отсутствует** | `AppConfig.cs` – `:13-14` (default 5001) | Сменить default на 5000 и проверять диапазон 1-65535. | Локальный |
| 4.4 | Валидация `PeerConfig.Address` | **Отсутствует** | `PeerConfig.cs` – `:9` (default `0.0.0.0`) | Требовать корректный IPv4-адрес и отклонять `0.0.0.0`. | Локальный |
| 4.5 | Post-deserialization валидация | **Отсутствует** | `ConfigLoader.cs` – `:11-18` | После десериализации вызывать `AppConfig.Validate()`. | Архитектурный |
| 4.6 | Strict YAML parsing | **Отсутствует** | `ConfigLoader.cs` – `:8` (`IgnoreUnmatchedProperties()`) | Удалить игнорирование или добавить логирование неизвестных полей. | Локальный |
| 4.7 | Атомарное сохранение конфига | **Отсутствует** | `ConfigLoader.cs` – `:23-24` (`Save` возвращает string, файла не пишет) | При записи в файл использовать `temp`-файл и `File.Replace`. | Локальный |
| 4.8 | `global.json` rollback policy | **Отсутствует** | `global.json` – `:4-5` | Изменить `rollForward` на `latestFeature` и `allowPrerelease` на `false`. | Локальный |

---

## Приоритет 5 – Тесты

| # | Описание | Статус | Действие | Уровень |
|---|-----------|--------|----------|----------|
| 5.1 | Добавить ссылки на недостающие проекты | **Реализовано** | Закрыто: `BGPLite.Tests.csproj:12-17` уже ссылается на `BGPLite.Api` и `BGPLite.Providers`. | – |
| 5.2 | Негативные тесты парсера | **Отсутствует** | Реализовать 8 тестов; reader уже бросает `BgpParseException` — тестам нужно лишь собрать битые буферы. | Тестовый |
| 5.3 | Тесты `PeerCommunityFilter` | **Отсутствует** | Добавить покрытие; учтите, что `DeniedWhenFilterActive` для без-community расходится с текущим поведением (см. P3.1). | Тестовый |
| 5.4 | Граничные значения `PrefixCodec` | **Отсутствует** | Длины 1, 7, 9, 23, 25, 31, >32, пустой буфер; тест `>32` требует сначала guard из P2.2. | Тестовый |
| 5.5 | Расширение тестов `BgpMessageTests` | **Отсутствует** | 2-byte ASN, extended-length атрибуты, пустые capabilities, порядок атрибутов. | Тестовый |
| 5.6 | Тесты `ConfigLoader.Save` | **Отсутствует** | Round-trip, ошибочный YAML, отсутствие файла, пустые секции (сейчас нулевое покрытие `Save`). | Тестовый |
| 5.7 | Тесты `BgpServer` и `BgpSession` | **Реализовано** | Закрыто: `BgpSessionShutdownTests.cs` — hold-timer expiry, NOTIFICATION перед закрытием, mock-TCP, конкурентная отправка. | – |

---

## Приоритет 6 – Архитектурные улучшения

| # | Описание | Статус | Действие | Уровень |
|---|-----------|--------|----------|----------|
| 6.1 | Реализовать `BgpTimers` | **Отсутствует** | Вынести таймеры (сейчас инлайн в `BgpSession`) в инжектируемый `BgpTimers` для keep-alive, hold-timer и connect-retry. | Архитектурный |
| 6.2 | FSM: добавить состояние `Active` | **Отсутствует** | Добавить в `BgpFsmState` и реализовать переходы согласно RFC 4271 (актуально только после outbound-connect; связано с 6.1). | Архитектурный |
| 6.3 | Async `PeerStore` | **Отсутствует** | Создать `IPeerStoreAsync` с `Task`-методами, адаптировать вызовы. | Архитектурный |
| 6.4 | Кэширование community-запросов | **Отсутствует** | Ввести in-memory кэш с TTL в `PeerCommunityFilter`. | Архитектурный |
| 6.5 | Добавить iBGP/eBGP различение | **Отсутствует** | Расширить `PeerConfig` полем `PeerType` и учитывать его в маршрутизации. | Архитектурный |
| 6.6 | MaxPrefix limit | **Отсутствует** | Добавить поле `MaxPrefix` в `PeerConfig`, проверять при добавлении префиксов. | Архитектурный |

---

## Приоритет 7 — Соответствие RFC (compliance gaps)

> Найдены отдельным RFC compliance-аудитом (2026-07-02). Не входят в базовый счёт 40 пунктов P1–P6. Полный §-by-§ разбор 11 relevant RFC — в [`RFC_COMPLIANCE.md`](RFC_COMPLIANCE.md).

| # | RFC | Описание | Статус | Действие | Уровень |
|---|-----|----------|--------|----------|---------|
| 7.1 | 2918 | Route Refresh message type 5 не реализован, но capability рекламируется → приём рвёт сессию | **Отсутствует (🔴 critical)** | Реализовать `BgpMessageType.RouteRefresh=5` + handler, либо убрать рекламу capability (`BgpSession.cs:912-913`). | Локальный |
| 7.2 | 6793 | Outbound AS_PATH всегда 4-byte (`BgpSession.cs:50,741`) → ломает 2-byte-only peers | **Отсутствует (🔴 critical)** | 2-byte fallback с AS_TRANS(23456) + AS4_PATH(17) для ASN>65535. | Архитектурный |
| 7.3 | 6793 | AS4_PATH(17)/AS4_AGGREGATOR(18) inbound reconstruction + AGGREGATOR(7) | **Отсутствует (🟠 major)** | Парсинг attr 17/18, реконструкция при AS_TRANS, AGGREGATOR read/write. | Архитектурный |
| 7.4 | 4271 | Нет валидации mandatory attributes в UPDATE (§5/§6.3) | **Отсутствует (🟠 major)** | Проверка наличия ORIGIN/AS_PATH/NEXT_HOP перед приёмом NLRI → NOTIFICATION Update Message Error. | Локальный |
| 7.5 | 5492 | Нет NOTIFICATION Unsupported Capability (Open subcode 7) | **Отсутствует (🟠 major)** | Шлёт subcode 7 + data при неприемлемой capability. | Локальный |
| 7.6 | 1997 | Well-known communities (NO_EXPORT/NO_ADVERTISE/NO_EXPORT_SUBCONFED) не обрабатываются | **Отсутствует (🟠 major)** | Семантика well-known при фильтрации/анонсировании. | Локальный |
| 7.7 | 8092 | Large Communities codec отсутствует (только константа type 32) | **Отсутствует (🟠 major)** | 12-byte (global:local:local) codec, `Route.LargeCommunities`. | Локальный |
| 7.8 | 2385 | TCP-MD5 session auth отсутствует | **Отсутствует (🟠 major)** | `TCP_MD5SIG` socket option + per-peer key в `PeerConfig`. | Архитектурный |
| 7.9 | 4360 | Extended Communities (type 16) отсутствует | **Отсутствует (🟡 useful)** | 8-byte codec, transitivity high-byte, well-known types (RT). | Локальный |

---

## RFC Compliance (сводная)

| RFC | Relevance | Готовность | Ключевой пробел |
|-----|-----------|-----------|-----------------|
| [4271](https://www.rfc-editor.org/rfc/rfc4271) BGP-4 | 🔴 required | ядро готово, ~17 none | нет валидации UPDATE (7.4), MRAI, route-selection §9 |
| [4893](https://www.rfc-editor.org/rfc/rfc4893) 4-octet AS | 🔴 required | ядро готово, ~7 none | 2-byte-peer fallback |
| [6793](https://www.rfc-editor.org/rfc/rfc6793) 4-byte+Aggr | 🔴 required | ~14 none | AS4_PATH/AS4_AGGREGATOR (7.2, 7.3) |
| [5492](https://www.rfc-editor.org/rfc/rfc5492) Capabilities | 🔴 required | ~15 done | subcode 7 (7.5) |
| [1997](https://www.rfc-editor.org/rfc/rfc1997) Communities | 🟡 useful | codec готов | well-known semantics (7.6) |
| [2918](https://www.rfc-editor.org/rfc/rfc2918) Route Refresh | 🟡 useful | cap ready, msg none | type 5 message (7.1) 🔴 |
| [4360](https://www.rfc-editor.org/rfc/rfc4360) Ext. Communities | 🟡 useful | none | codec (7.9) |
| [8092](https://www.rfc-editor.org/rfc/rfc8092) Large Comm. | 🟡 useful | none | codec (7.7) |
| [2385](https://www.rfc-editor.org/rfc/rfc2385) TCP-MD5 | 🟡 useful | none | session auth (7.8) |
| [4760](https://www.rfc-editor.org/rfc/rfc4760) MP-BGP | ⚪ optional | IPv4-only | MP_REACH/UNREACH (roadmap #14) |
| [2545](https://www.rfc-editor.org/rfc/rfc2545) IPv6 MP | ⚪ optional | none | IPv6 full stack (roadmap #14/#15) |

Не нужны для route-server: RFC 4456 (route reflection), 5065 (confederations), 4364 (MPLS VPN), 4761 (VPLS), 6286 (single speaker), 4273 (SNMP).

---

## План действий
1. ~~Срочно реализовать пункты P1~~ (закрыто в PR #5 / #18-#21, кроме hold-timer DI — P6.1).
2. Затем выполнить все проверки протокольных условий (P2). Начать с P2.4 — уже закрыт (`9709c69`).
3. Параллельно исправить критические проблемы маршрутизации (P3) и добавить валидацию конфигурации (P4).
4. После исправления кода написать недостающие тесты (P5; P5.1 и P5.7 уже закрыты).
5. По завершении внедрить архитектурные улучшения из P6.
6. **RFC compliance (P7):** сначала critical — 7.1 Route Refresh (capability/message mismatch рвёт сессию) и 7.2 AS_PATH 2-byte fallback; затем major (7.3–7.8).

---

*Этот документ поддерживается в актуальном состоянии. При необходимости добавляйте новые пункты или меняйте приоритеты.*

## Приоритет 1: Критические ошибки сессий и потокобезопасности

> **Все пункты закрыты** в PR #5 (`bd924df`, `c58dc8b`, `7ba07c7`, `968f2e4`, `5387dd3`, `037096e`) + PR #18/#20 (`SessionKey`). P1.3 (Hold Timer) — частично: таймер работает, рефакторинг в `BgpTimers` через DI отложен (P6.1).

### 1.1 Гонка при замене сессий — ✅ Закрыто
**Файл:** `BgpServer.cs:187-221` (TryAdd + TryUpdate CAS с retry-циклом), `:263-271` (atomic remove через `ICollection<KeyValuePair>.Remove`)
**Решение:** атомарная замена сессии по ключу `SessionKey` (IP+порт, `SessionKey.cs:21`) — `TryAdd` с retry и CAS, атомарное удаление только если запись принадлежит закрываемой сессии (`RemoveSessionIfOwner`).
**Коммиты:** `c58dc8b` (PR #5); ключ `SessionKey` — PR #20 / #18.

### 1.2 Параллельная запись в `_stream` — ✅ Закрыто
**Файл:** `BgpSession.cs:286` (`_sendLock`), `:848-882` (`SendMessageAsync`)
**Решение:** `SemaphoreSlim _sendLock` берётся внутри `SendMessageAsync` — единственной точки отправки; все пути (keepalive, initial routes, NOTIFICATION, refresh) проходят через неё.
**Коммит:** `bd924df` (PR #5).

### 1.3 Hold Timer — ⚠️ Частично
**Файл:** `BgpSession.cs:301` (init `Interlocked.Exchange`), `:326` (reset на receive), `:351-377` (`HoldTimerLoopAsync`), `:362-369` (compare + NOTIFICATION CAS)
**Что сделано:** hold-таймер реализован инлайн на `Interlocked _lastReceivedTicks`; по истечении — NOTIFICATION (HoldTimerExpired) + Cease CAS. `_negotiatedHoldTime` используется (`:294`, `:353`, `:365`).
**Что осталось:** вынести логику таймеров в инжектируемый класс `BgpTimers` через DI (P6.1, issue #10). Примечание: класс `BgpTimers` никогда не существовал в кодовой базе — прежняя формулировка плана про «мёртвый код `BgpTimers.cs`» была неверной.

### 1.4 NOTIFICATION при штатном завершении — ✅ Закрыто
**Файл:** `BgpSession.cs:254-258`, `:270-274` (Cease CAS в `catch`/`finally`), `:977-996` (`NotifyCeaseAsync`)
**Решение:** best-effort Cease NOTIFICATION через `Interlocked.CompareExchange` в `catch(Exception)` и `finally`; также CAS для `RemoteNotification` (`:345`), `HoldTimerExpired` (`:366`), `SilentClose` (`:1013`).
**Коммиты:** feat `037096e` (Cease) + teardown races `7ba07c7`, `968f2e4`, `5387dd3` (PR #5).

### 1.5 `_state` без барьеров памяти — ✅ Закрыто
**Файл:** `BgpSession.cs:34` (`volatile BgpFsmState _state`), `:58` (`IsEstablished`)
**Решение:** поле объявлено `volatile`; дополнительно `Interlocked` для `_teardownReason` (`:46`), `_lastReceivedTicks` (`:54`), `_disposed` (`:47`).
**Коммит:** `bd924df` (PR #5).

---

## Приоритет 2: Протокольные ошибки

### 2.1 Порядок path attributes
**Файл:** `BgpMessageWriter.cs:130-134` (цикл `foreach` в `WriteUpdate` 113-141)
**Проблема:** Writer не сортирует атрибуты. RFC 4271 §5: ORIGIN → AS_PATH → NEXT_HOP → MED → LOCAL_PREF.
**Исправление:** сортировать `PathAttributes` перед записью; в `ParseUpdate` валидировать порядок входящих атрибутов.

### 2.2 Валидация `PrefixCodec` на `length > 32`
**Файл:** `PrefixCodec.cs:21` (Encode, negative shift `24 - i*8` при `i >= 4`), `:35, 37` (Decode shift+mask)
**Проблема:** `length > 32` вызывает OOB-запись и неверную маску.
**Исправление:** `if (length > 32) throw new ArgumentOutOfRangeException(...)` в `Encode`/`Decode` и в `EncodeList`/`DecodeList` (`:41-60`).

### 2.3 Усечение в OPEN-кодировании
**Файл:** `BgpMessageWriter.cs:57, 88, 93` (три места приведения к `byte`)
**Проблема:** `optParamsLen`/`capDataLen` приводятся к `byte`. Если >255 — declared length ≠ реальные байты → malformed frame.
**Исправление:** валидировать ≤ 255 (или 2-байтовое поле / разделение optional parameters, RFC 5492).

### 2.4 `ReadAsPath` смешивает ASN размеры — ✅ Закрыто
**Файл:** `AttributeHelper.cs:22-50` (guard `:35-36`)
**Решение:** добавлен guard `if (offset + segBytes > attr.Data.Length) break;` — предотвращает OOB-чтение при 4-byte ASN с недостатком данных.
**Коммит:** `9709c69` (в `main`).

### 2.5 Валидация атрибутов при чтении
**Файл:** `AttributeHelper.cs:7-10` (`ReadOrigin`), `:82-85` (`ReadNextHop`), `:99-106` (`ReadCommunities`); `BgpMessageReader.cs:192-193` (`ParseAttribute` без bounds-check перед `Slice`)
**Проблема:** нет проверок `attr.Data.Length` (Origin ≥1, NextHop ≥4, Communities %4 == 0).
**Исправление:** валидация длины в каждом методе; `BgpParseException` (UPDATE 3/1) при нарушении.

### 2.6 Валидация reserved attribute flag bits
**Файл:** `BgpMessageReader.cs:174-196` (`ParseAttribute`); флаги — `BgpConstants.cs:57-60`
**Проблема:** Бит 0x08 в флагах не проверяется. RFC 4271 требует его = 0.
**Исправление:** в `ParseAttribute` (reader): `if ((flags & 0x08) != 0) throw BgpParseException`; в writer всегда сбрасывать 0x08. (`PathAttribute.cs` — только data-holder, проверки там нет.)

### 2.7 Hold time high byte
**Файл:** `BgpMessageReader.cs:63`
**Проблема:** Высокий октет hold time не проверяется (RFC: MUST be zero).
**Исправление:** проверять `payload[3] == 0`; иначе NOTIFICATION (OPEN Message Error / `UnacceptableHoldTime`, `BgpConstants.cs:39` — сейчас не используется).

### 2.8 Валидация OPEN payload length
**Файл:** `BgpMessageReader.cs:55-69` (точнее после `:68`)
**Проблема:** Трейлинговые байты игнорируются (есть только `>=` guard, нет точного `!=`). RFC 4271 §6.2: length должен точно соответствовать.
**Исправление:** `if (payload.Length != 10 + optParamsLen) → NOTIFICATION`.

### 2.9 `BgpConstants.IPAddressToUint` без IPv4 guard
**Файл:** `BgpConstants.cs:94-98`
**Проблема:** IPv6 адрес (16 байт) молча обрезается до 4.
**Исправление:** `if (address.AddressFamily != AddressFamily.InterNetwork) throw`.

---

## Приоритет 3: Маршрутизация

### 3.1 Утечка маршрутов через community-фильтр
**Файл:** `PeerCommunityFilter.cs:22-23`
**Проблема:** Маршруты без community обходят фильтр (`return true`).
**Исправление:** Если `allowed.Count > 0` и `route.Communities.Length == 0` → `return false`; только при пустом фильтре → `return true`.

### 3.2 Мутабельные массивы в `Route`
**Файл:** `Route.cs:8-9`
**Проблема:** `AsPath` и `Communities` — `uint[]` с `init`. Мутируемое содержимое (init защищает только ссылку).
**Исправление:** `ImmutableArray<uint>` или `IReadOnlyList<uint>`; клонировать входные массивы в конструкторе.

### 3.3 `RouteTable.AddOrUpdate` ненадёжен
**Файл:** `RouteTable.cs:11-18`
**Проблема:** `ConcurrentDictionary.AddOrUpdate` вызывает add-factory несколько раз. `added` невалиден; возможна лишняя аллокация `Route`.
**Исправление:** `TryAdd` + `_routes[key] = value` через lock, либо `GetOrAdd` + проверку.

### 3.4 Валидация prefix length и masking
**Файл:** `Route.cs:5-6`, `RouteTable.cs:14`
**Проблема:** Префиксы не маскируются в `Route` (маскирование есть только в `ExactUnionPrefixAggregator`). Дубликаты ключей при host bits.
**Исправление:** В конструкторе `Route`: `Prefix = Prefix & (0xFFFFFFFF << (32 - PrefixLength))`; валидировать `0 <= Length <= 32`.

### 3.5 Longest-prefix-match
**Файл:** `RouteTable.cs` (класс)
**Проблема:** Нет метода `Lookup(uint address)`. Только exact match `Get`.
**Исправление:** `Route? Lookup(uint address)` — можно через `Enumerate()` (линейный поиск) или Patricia-trie.

---

## Приоритет 4: Конфигурация

### 4.1 Валидация RouterId
**Файл:** `BgpConfig.cs:11-12`
**Проблема:** Default `"0.0.0.0"` нарушает RFC 4271 §6.8.
**Исправление:** `RouterId` required; в `GetRouterIdAddress()` валидировать ≠ `0.0.0.0` и ≠ IPv6.

### 4.2 Валидация HoldTime/KeepAlive
**Файл:** `BgpConfig.cs:14-18`
**Проблема:** Любой int принимается. RFC: 0 или ≥3.
**Исправление:** `HoldTime != 0 && HoldTime < 3` → throw; `KeepAlive != 0 && KeepAlive < 1` → throw; `KeepAlive <= HoldTime/3`.

### 4.3 Валидация ApiPort
**Файл:** `AppConfig.cs:13-14`
**Проблема:** Default `5001`, документация говорит `5000`. Нет range-валидации.
**Исправление:** Default `5000`; валидировать `1 <= ApiPort <= 65535`.

### 4.4 Валидация PeerConfig.Address
**Файл:** `PeerConfig.cs:9`
**Проблема:** Default `"0.0.0.0"` невалиден как peer address.
**Исправление:** `Address` required; `IPAddress.TryParse` при загрузке.

### 4.5 Post-deserialization валидация
**Файл:** `ConfigLoader.cs:11-18`
**Проблема:** После десериализации нет проверок.
**Исправление:** `AppConfig.Validate()`; вызывать из `Load`/`LoadFromText`.

### 4.6 Strict YAML parsing
**Файл:** `ConfigLoader.cs:8`
**Проблема:** `IgnoreUnmatchedProperties` тихо глотает опечатки.
**Исправление:** убрать или strict mode; минимум — логировать неизвестные свойства.

### 4.7 Атомарное сохранение конфига
**Файл:** `ConfigLoader.cs:23-24`
**Проблема:** `Save` сейчас возвращает строку и не пишет на диск (прежняя формулировка про `File.WriteAllText` устарела). Атомарной записи в файл нет.
**Исправление:** при записи в файл — `path + ".tmp"` → `File.Replace`/`File.Move`.

### 4.8 `global.json` rollback policy
**Файл:** `global.json:4-5`
**Проблема:** `rollForward: latestMajor` + `allowPrerelease: true` — пиннинг SDK (10.0.0) бессмысленен.
**Исправление:** `rollForward: latestFeature`, `allowPrerelease: false`.

---

## Приоритет 5: Тесты

### 5.1 Ссылки на проекты — ✅ Закрыто
**Файл:** `BGPLite.Tests/BGPLite.Tests.csproj:12-17`
**Решение:** `BGPLite.Tests` уже ссылается на `BGPLite.Api` и `BGPLite.Providers` (прежнее утверждение «нет ссылок на Api/Providers» устарело). Корневой `BGPLite` (Exe) отдельной ссылки не требует.

### 5.2 Негативные тесты парсера
**Проблема:** 8 критических путей без тестов (`BgpMessageTests.cs:212, 220` — единственные негативные).
**Добавить:** `ReadMessage_UnknownType_Throws`, `ReadMessage_InvalidLength_Throws`, `ReadMessage_Incomplete_Throws`, `ReadMessage_OpenTooShort_Throws`, `ReadMessage_UnsupportedVersion_Throws`, `ReadMessage_UpdateTooShort_Throws`, `ReadMessage_NotificationTooShort_Throws`, `ReadMessage_UpdateMissingAttributes_Throws`. Reader уже бросает `BgpParseException` — тестам лишь собрать битые буферы.

### 5.3 Тесты `PeerCommunityFilter`
**Проблема:** нулевое покрытие (`PeerCommunityFilter.cs:16-32`).
**Добавить:** `RouteWithAllowedCommunity_ReturnsTrue`, `RouteWithoutCommunity_DeniedWhenFilterActive` (⚠️ расходится с текущим поведением P3.1), `RouteWithNoOverlap_ReturnsFalse`, `EmptyAllowedSet_AllRoutesPass`.

### 5.4 Граничные значения `PrefixCodec`
**Проблема:** только byte-aligned длины (`PrefixCodecTests.cs`).
**Добавить:** длины 1, 7, 9, 23, 25, 31; host bits set (masking); `length > 32` → exception (требует guard из P2.2); пустой буфер.

### 5.5 Расширение тестов `BgpMessageTests`
**Добавить:** roundtrip 2-byte ASN paths; extended-length атрибут (>255 байт); OPEN с пустыми capabilities; порядок атрибутов; содержимое capability data (частично покрыто в `GracefulRestartTests.cs`).

### 5.6 Тесты `ConfigLoader.Save`
**Проблема:** только позитивные `LoadFromText` (`ConfigurationTests.cs`); `Save` — нулевое покрытие.
**Добавить:** `SaveLoad_Roundtrip_PreservesAllFields`, `Load_InvalidYaml_Throws`, `Load_MissingFile_Throws`, `LoadFromText_EmptyBgpSection_UsesDefaults`.

### 5.7 Тесты `BgpServer` и `BgpSession` — ✅ Закрыто
**Файл:** `BGPLite.Tests/BgpSessionShutdownTests.cs`
**Решение:** реализованы все четыре под-пункта — hold-timer expiry (`:136`), NOTIFICATION перед закрытием (`:56`), mock-TCP интеграция, конкурентная отправка (`:438`, через `ConcurrentDictionary` race + `_sendLock`).

---

## Приоритет 6: Архитектурные улучшения

### 6.1 Реализовать `BgpTimers`
**Проблема:** таймеры (keep-alive, hold, connect-retry) реализованы инлайн в `BgpSession` (`BgpSession.cs:51-54`, `:291`, `:303`, `:351`), а не вынесены в инжектируемый компонент.
**Исправление:** класс `BgpTimers` через DI; используется в `BgpSession`. (Прежняя формулировка «класс существует, но мёртвый» неверна — `BgpTimers` никогда не создавался.)

### 6.2 FSM: добавить состояние `Active`
**Проблема:** RFC 4271 §8 требует 6 состояний (`BgpFsmState.cs:3-10`).
**Исправление:** `BgpFsmState.Active`; переходы Connect → Active (connect failure), Active → Connect (ConnectRetry). Актуально только после реализации outbound-connect; связано с 6.1.

### 6.3 Async PeerStore
**Проблема:** `IPeerStore` (`IPeerStore.cs:3-19`) полностью синхронный, блокирует session thread.
**Исправление:** async-версии (`GetPeerByIpAsync` и т.д.) или `ValueTask`.

### 6.4 Кэширование community-запросов
**Проблема:** `PeerCommunityFilter` (`:19-31`) делает DB-запрос на каждый (route, peer).
**Исправление:** in-memory кэш с TTL на уровне сессии; инвалидация при обновлении subscriptions.

### 6.5 Добавить iBGP/eBGP различение
**Проблема:** нет типа пира (`PeerConfig.cs`).
**Исправление:** `PeerConfig.PeerType: IBGP | EBGP`; влияет на next-hop handling и AS_PATH prepend.

### 6.6 MaxPrefix limit
**Проблема:** нет ограничения префиксов на пира (`PeerConfig.cs`).
**Исправление:** `PeerConfig.MaxPrefix: uint?`; при превышении → NOTIFICATION (Cease / Max Prefixes Exceeded).

---

## Порядок реализации

| Этап | Приоритет | Описание |
|------|-----------|----------|
| 1 | P1 | ✅ Гонки сессий, send lock, NOTIFICATION, volatile state (hold-timer DI → P6.1) |
| 2 | P2 | Протокольная валидация (2.4 ✅; остальное: attributes, prefix, OPEN) |
| 3 | P3 | Community filter, Route immutability, prefix masking, LPM |
| 4 | P4 | Конфигурация (validation, strict YAML, atomic save, global.json) |
| 5 | P5 | Тесты (5.1 ✅, 5.7 ✅; остальное: negative paths, codec, integration) |
| 6 | P6 | Архитектура (BgpTimers, FSM Active, async PeerStore, cache) |

---

## Зависимости между задачами

```
1.1 (гонка сессий) ──┐
1.2 (send lock)    ──┤── 2.1 (порядок attributes)
1.3 (hold timer)   ──┤── 5.7 ✅ (интеграционные тесты)
1.4 (NOTIFICATION) ──┤
1.5 (volatile)     ──┘

2.2 (PrefixCodec)  ──┐
2.3 (OPEN encoding) ─┤── 5.4 (граничные тесты)
2.5 (attribute len) ─┘── 5.2 (негативные тесты)

3.1 (community filter) ──┐
3.2 (Route immutable)  ──┤── 5.3 (тесты фильтра)
3.3 (AddOrUpdate)      ──┤
3.4 (prefix masking)   ──┤
3.5 (LPM)              ──┘

4.1-4.5 (config) ── 5.6 (тесты конфига)
```
