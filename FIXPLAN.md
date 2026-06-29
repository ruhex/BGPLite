# # BGPLite — Обновлённый план исправлений

## Общий обзор

- **Всего пунктов:** 40
- **Полностью реализовано:** 2 (5 %)
- **Частично реализовано:** 2 (5 %)
- **Не реализовано:** 36 (90 %)

Этот план переупорядочивает задачи в соответствии с проведённым аудитом и приоритизирует их для дальнейшей разработки.

---

## Приоритет 1 – Критические ошибки сессий и потокобезопасность

| # | Описание | Статус | Файл / Строки | Действие | Уровень исправления |
|---|-----------|--------|---------------|----------|---------------------|
| 1.1 | Гонка при замене сессий | **Отсутствует** | `BGPLite.Server/BgpServer.cs` – строки 141‑142 | Ввести атомарную замену (`TryAdd` + generation‑counter) или синхронизацию. | Архитектурный |
| 1.2 | Параллельная запись в `_stream` | **Реализовано** | `BGPLite.Server/BgpSession.cs` – строки 45‑59, 771‑775 | – | – |
| 1.3 | Отсутствует Hold Timer | **Частично реализовано** | `BGPLite.Server/BgpSession.cs` – строки 286‑304 (таймер реализован, но класс `BgpTimers` не используется) | Перенести логику в `BgpTimers` и внедрить через DI. | Архитектурный |
| 1.4 | NOTIFICATION при штатном завершении | **Реализовано** | `BGPLite.Server/BgpSession.cs` – строки 210‑215, `BgpServer.cs` – строки 85‑93 | – | – |
| 1.5 | `_state` без барьеров памяти | **Отсутствует** | `BGPLite.Server/BgpSession.cs` – строки 28‑40 | Сделать поле `volatile` или использовать `Interlocked` при переходах. | Локальный |

---

## Приоритет 2 – Протокольные ошибки

| # | Описание | Статус | Файл / Строки | Действие | Уровень |
|---|-----------|--------|---------------|----------|----------|
| 2.1 | Порядок path attributes | **Отсутствует** | `BGPLite.Protocol/BgpMessageWriter.cs` – строки 129‑134 | Добавить сортировку атрибутов согласно RFC 4271. | Общий |
| 2.2 | Валидация `PrefixCodec` на `length > 32` | **Отсутствует** | `BGPLite.Protocol/PrefixCodec.cs` – строки 7‑24 | Вставить проверку и бросить `ArgumentOutOfRangeException`. | Локальный |
| 2.3 | Усечение в OPEN‑кодировании | **Отсутствует** | `BGPLite.Protocol/BgpMessageWriter.cs` – строки 40‑61 | Проверять, что длина `optParamsLen` и `capDataLen` ≤ 255, либо использовать 2‑байтовое поле. | Локальный |
| 2.4 | `ReadAsPath` смешивает ASN размеры | **Отсутствует** | `BGPLite.Protocol/AttributeHelper.cs` – строки 22‑50 | При недостаточном количестве байтов бросать `BgpParseException`. | Локальный |
| 2.5 | Валидация атрибутов при чтении | **Отсутствует** | `BGPLite.Protocol/AttributeHelper.cs` – строки 7‑10, 82‑86, 99‑106 | Добавить проверки длины данных. | Локальный |
| 2.6 | Валидация reserved attribute flag bits | **Отсутствует** | `BGPLite.Protocol/BgpMessageReader.cs` – строки 174‑196 | Проверять, что бит 0x08 = 0. | Локальный |
| 2.7 | Hold time high byte | **Отсутствует** | `BGPLite.Protocol/BgpMessageReader.cs` – строки 63‑66 | Проверять, что старший байт = 0. | Локальный |
| 2.8 | Валидация OPEN payload length | **Отсутствует** | `BGPLite.Protocol/BgpMessageReader.cs` – строки 55‑69 | Убедиться, что длина exactly matches `optParamsLen`. | Локальный |
| 2.9 | `BgpConstants.IPAddressToUint` без IPv4 guard | **Отсутствует** | `BGPLite.Protocol/BgpConstants.cs` – строки 94‑98 | Проверять `AddressFamily.InterNetwork`. | Локальный |

---

## Приоритет 3 – Маршрутизация

| # | Описание | Статус | Файл / Строки | Действие | Уровень |
|---|-----------|--------|---------------|----------|----------|
| 3.1 | Утечка маршрутов через community‑фильтр | **Отсутствует** | `BGPLite.Routing/PeerCommunityFilter.cs` – строки 18‑23 | Возвратить `false` когда фильтр включён и маршрут без community. | Локальный |
| 3.2 | Мутабельные массивы в `Route` | **Отсутствует** | `BGPLite.Routing/Route.cs` – строки 8‑9 | Перейти на `IReadOnlyList<uint>` или копировать массивы в конструкторе. | Общий |
| 3.3 | `RouteTable.AddOrUpdate` ненадёжен | **Отсутствует** | `BGPLite.Routing/RouteTable.cs` – строки 11‑18 | Заменить на `TryAdd` + lock или `GetOrAdd` с проверкой. | Локальный |
| 3.4 | Валидация prefix length и masking | **Отсутствует** | `BGPLite.Routing/Route.cs` – строки 5‑6 (маскирование отсутствует) | Применить маскирование и проверку диапазона. | Локальный |
| 3.5 | Longest‑prefix‑match | **Отсутствует** | — | Добавить метод `Lookup(uint address)` в `RouteTable`. Может быть реализовано через линейный поиск или Patricia‑три. | Архитектурный |

---

## Приоритет 4 – Конфигурация

| # | Описание | Статус | Файл / Строки | Действие | Уровень |
|---|-----------|--------|---------------|----------|----------|
| 4.1 | Валидация RouterId | **Отсутствует** | `BgpConfig.cs` – строка 12 (default `0.0.0.0`) | Сделать поле обязательным и проверять ≠ `0.0.0.0` при загрузке. | Локальный |
| 4.2 | Валидация HoldTime/KeepAlive | **Отсутствует** | `BgpConfig.cs` – строки 14‑18 | Проверять `HoldTime` ≥ 3 (если ≠ 0) и `KeepAlive` ≥ 1, а также `KeepAlive ≤ HoldTime/3`. | Локальный |
| 4.3 | Валидация ApiPort | **Отсутствует** | `AppConfig.cs` – строка 13 (default 5001) | Сменить default на 5000 и проверять диапазон 1‑65535. | Локальный |
| 4.4 | Валидация `PeerConfig.Address` | **Отсутствует** | `PeerConfig.cs` – строка 9 (default `0.0.0.0`) | Требовать корректный IPv4‑адрес и отклонять `0.0.0.0`. | Локальный |
| 4.5 | Post‑deserialization валидация | **Отсутствует** | `ConfigLoader.cs` – строки 11‑19 | После десериализации вызвать `AppConfig.Validate()`. | Архитектурный |
| 4.6 | Strict YAML parsing | **Отсутствует** | `ConfigLoader.cs` – строка 8 (`IgnoreUnmatchedProperties()`) | Удалить игнорирование или добавить логирование неизвестных полей. | Локальный |
| 4.7 | Атомарное сохранение конфига | **Отсутствует** | `ConfigLoader.cs` – строки 23‑25 (возврат строки) | При записи в файл использовать `temp`‑файл и `File.Replace`. | Локальный |
| 4.8 | `global.json` rollback policy | **Отсутствует** | `global.json` – строки 3‑5 | Изменить `rollForward` на `latestFeature` и `allowPrerelease` на `false`. | Локальный |

---

## Приоритет 5 – Тесты

| # | Описание | Статус | Действие | Уровень |
|---|-----------|--------|----------|----------|
| 5.1 | Добавить ссылки на недостающие проекты | **Отсутствует** | Добавить `<ProjectReference>` к `BGPLite` в `BGPLite.Tests.csproj`. | Архитектурный |
| 5.2 | Негативные тесты парсера | **Отсутствует** | Реализовать 8 тестов, перечисленных в оригинальном планe. | Тестовый |
| 5.3 | Тесты `PeerCommunityFilter` | **Отсутствует** | Добавить покрытие всех вариантов списка и флага. | Тестовый |
| 5.4 | Граничные значения `PrefixCodec` | **Отсутствует** | Добавить параметризованные тесты для длин 1,7,9,23,25,31,>32, пустой буфер. | Тестовый |
| 5.5 | Расширение тестов `BgpMessageTests` | **Отсутствует** | Добавить сценарии с 2‑byte ASN, расширенными атрибутами, пустыми capabilities и т.д. | Тестовый |
| 5.6 | Тесты `ConfigLoader.Save` | **Отсутствует** | Проверить round‑trip, ошибочный YAML, отсутствие файла, пустые секции. | Тестовый |
| 5.7 | Тесты `BgpServer` и `BgpSession` | **Отсутствует** | Интеграционные тесты: hold‑timer expiry, NOTIFICATION перед закрытием, конкурентная отправка. | Тестовый |

---

## Приоритет 6 – Архитектурные улучшения

| # | Описание | Статус | Действие | Уровень |
|---|-----------|--------|----------|----------|
| 6.1 | Реализовать `BgpTimers` | **Отсутствует** | Инжектировать класс в `BgpServer`/`BgpSession`, использовать его для keep‑alive, hold‑timer и connect‑retry. | Архитектурный |
| 6.2 | FSM: добавить состояние `Active` | **Отсутствует** | Добавить в `BgpFsmState` и реализовать переходы согласно RFC 4271. | Архитектурный |
| 6.3 | Async `PeerStore` | **Отсутствует** | Создать `IPeerStoreAsync` с `Task`‑методами, адаптировать вызовы. | Архитектурный |
| 6.4 | Кэширование community‑запросов | **Отсутствует** | Ввести in‑memory кэш с TTL в `PeerCommunityFilter`. | Архитектурный |
| 6.5 | Добавить iBGP/eBGP различение | **Отсутствует** | Расширить `PeerConfig` полем `PeerType` и учитывать его в маршрутизации. | Архитектурный |
| 6.6 | MaxPrefix limit | **Отсутствует** | Добавить поле `MaxPrefix` в `PeerConfig`, проверять при добавлении префиксов. | Архитектурный |

---

## План действий
1. **Срочно** реализовать пункты Приоритета 1 (гонка сессий, атомарность `_state`).
2. Затем выполнить все проверки протокольных условий (Приоритет 2).
3. Параллельно исправить критические проблемы маршрутизации (Приоритет 3) и добавить валидацию конфигурации (Приоритет 4).
4. После исправления кода написать недостающие тесты (Приоритет 5).
5. По завершении внедрить архитектурные улучшения из Приоритета 6.

---

*Этот документ будет поддерживаться в актуальном состоянии. При необходимости добавляйте новые пункты или меняйте приоритеты.*

## Приоритет 1: Критические ошибки сессий и потокобезопасность

### 1.1 Гонка при замене сессий
**Файл:** `BgpServer.cs:122,142`
**Проблема:** `_sessions[peerAddress] = session` безусловно перезаписывает запись. Finally-блок старой сессии удалит новую.
**Исправление:**
- Использовать `TryAdd` + атомарную замену через generation counter
- При обнаружении существующей сессии — отправить NOTIFICATION/Cease старой, дождаться завершения, затем создать новую
- Или: в `RunSessionAsync` проверять `_sessions[peerAddress] == this` перед `TryRemove`

### 1.2 Параллельная запись в `_stream`
**Файл:** `BgpSession.cs:208`
**Проблема:** `_sendLock` берётся только в `RefreshRoutesAsync`. Keepalive, initial routes, NOTIFICATION пишут без блокировки. `NetworkStream` не потокобезопасен.
**Исправление:**
- Перенести `_sendLock` внутрь `SendMessageAsync` (брать/освобождать там)
- Или: ввести `Channel<BgpMessage>` + единственный writer-loop
- Все пути отправки должны проходить через одну точку синхронизации

### 1.3 Отсутствует Hold Timer
**Файл:** `BgpSession.cs`, `BgpTimers.cs` (мёртвый код)
**Проблема:** RFC 4271 требует NOTIFICATION (Hold Timer Expired, subcode 4) + Idle при истечении hold-таймера. `_negotiatedHoldTime` не используется.
**Исправление:**
- Реализовать `BgpTimers` с `HoldTimer` и `ConnectRetryTimer`
- При каждом полученном сообщении сбрасывать hold-таймер
- По истечении: `SendNotificationAsync(4, 0)` → `_cts.Cancel()` → FSM → Idle
- Удалить мёртвый код `BgpTimers.cs`, реализовать логику

### 1.4 NOTIFICATION при штатном завершении
**Файл:** `BgpSession.cs:190-193`
**Проблема:** Внешний `catch (Exception)` не отправляет NOTIFICATION (Cease). RFC 4271 §8.1.
**Исправление:**
- В `catch (Exception)` и в `finally`: попытаться `SendNotificationAsync(6, 0)`, поглотить IO-ошибки
- Гарантировать NOTIFICATION перед `socket.Close()`

### 1.5 `_state` без барьеров памяти
**Файл:** `BgpSession.cs:37,752`
**Проблема:** `IsEstablished` читает `_state` из другого потока без `Volatile.Read`. JIT может кешировать.
**Исправление:**
- Заменить `_state` на `volatile` поле или использовать `Volatile.Read(ref _state)`
- Или: ввести `Interlocked.Exchange`/`Interlocked.CompareExchange` для транзакций состояния

---

## Приоритет 2: Протокольные ошибки

### 2.1 Порядок path attributes
**Файл:** `BgpMessageWriter.cs:113-141`
**Проблема:** Writer не сортирует атрибуты. RFC 4271 §5: ORIGIN → AS_PATH → NEXT_HOP → MED → LOCAL_PREF.
**Исправление:**
- Добавить константный порядок атрибутов (enum или массив)
- В `WriteUpdate` сортировать `PathAttributes` перед записью
- В `ParseUpdate` валидировать порядок входящих атрибутов

### 2.2 Валидация `PrefixCodec` на `length > 32`
**Файл:** `PrefixCodec.cs:16-22, 32-38`
**Проблема:** `length > 32` вызывает OOB-запись (сдвиг `24 - i*8` при `i>=4`) и неверную маску.
**Исправление:**
- Добавить `if (length > 32) throw new ArgumentOutOfRangeException(...)` в `Encode` и `Decode`
- Добавить guard в `EncodeList`/`DecodeList`

### 2.3 Усечение в OPEN-кодировании
**Файл:** `BgpMessageWriter.cs:57,88,93`
**Проблема:** `optParamsLen` и `capDataLen` приводятся к `byte`. Если >255 — declared length ≠ реальные байты.
**Исправление:**
- Использовать 2-байтное поле для длины (или валидировать что ≤255)
- Или: разделить на multiple optional parameters (RFC 5492)

### 2.4 `ReadAsPath` смешивает ASN размеры
**Файл:** `AttributeHelper.cs:32-44`
**Проблема:** При `fourByteAsn && offset + 4 > attr.Data.Length` inner loop падает в ветку 16-bit.
**Исправление:**
- Если `fourByteAsn` и данных недостаточно для 4-byte ASN — бросить `BgpParseException`
- Не смешивать размеры в одном сегменте

### 2.5 Валидация атрибутов при чтении
**Файл:** `AttributeHelper.cs:8-11, 79-82, 96-103`
**Проблема:**
- `ReadOrigin`: нет проверки `attr.Data.Length >= 1`
- `ReadNextHop`: нет проверки `attr.Data.Length >= 4`
- `ReadCommunities`: нет проверки `attr.Data.Length % 4 == 0`
**Исправление:**
- Добавить валидацию длины в каждом методе
- Бросать `BgpParseException` (UPDATE 3/1) при нарушении

### 2.6 Валидация reserved attribute flag bits
**Файл:** `PathAttribute.cs`
**Проблема:** Бит 0x08 в флагах не проверяется. RFC 4271 требует его = 0.
**Исправление:**
- В `ParseAttribute`: проверять `(flags & 0x08) == 0`, иначе `BgpParseException`
- В `WriteAttribute`: всегда сбрасывать бит 0x08

### 2.7 Hold time high byte
**Файл:** `BgpMessageReader.cs:63`
**Проблема:** Высокий октет hold time не проверяется (RFC: MUST be zero).
**Исправление:**
- Проверять `payload[3] == 0` при чтении hold time
- Иначе NOTIFICATION (OPEN Message Error / Unacceptable Hold Time)

### 2.8 Валидация OPEN payload length
**Файл:** `BgpMessageReader.cs:68-69`
**Проблема:** Трейлинговые байты игнорируются. RFC 4271 §6.2: length должен точно соответствовать.
**Исправление:**
- Проверять `payload.Length == 10 + optParamsLen`, иначе NOTIFICATION

### 2.9 `BgpConstants.IPAddressToUint` без IPv4 guard
**Файл:** `BgpConstants.cs:86-90`
**Проблема:** IPv6 адрес → 16 байт, молча обрезается до 4.
**Исправление:**
- `if (address.AddressFamily != AddressFamily.InterNetwork) throw`

---

## Приоритет 3: Маршрутизация

### 3.1 Утечка маршрутов через community-фильтр
**Файл:** `PeerCommunityFilter.cs:22-23`
**Проблема:** Маршруты без community обходят фильтр (`return true`).
**Исправление:**
- Если `allowed.Count > 0` и `route.Communities.Length == 0` → `return false`
- Только если фильтр не настроен (allowed пуст) → `return true`

### 3.2 Мутабельные массивы в `Route`
**Файл:** `Route.cs:8-9`
**Проблема:** `AsPath` и `Communities` — `uint[]` с `init`. Мутируемое содержимое.
**Исправление:**
- Заменить на `ImmutableArray<uint>` или `IReadOnlyList<uint>`
- При создании `Route` — клонировать входные массивы

### 3.3 `RouteTable.AddOrUpdate` ненадёжен
**Файл:** `RouteTable.cs:11-18`
**Проблема:** `ConcurrentDictionary.AddOrUpdate` вызывает add-factory несколько раз. `added` невалиден.
**Исправление:**
- Использовать `TryAdd` + `_routes[key] = value` через lock
- Или: `GetOrAdd` + проверку `ContainsKey` до/после

### 3.4 Валидация prefix length и masking
**Файл:** `Route.cs:5-6`, `RouteTable.cs:14`
**Проблема:** Префиксы не маскируются. Дубликаты ключей.
**Исправление:**
- В `Route` конструкторе: `Prefix = Prefix & (0xFFFFFFFF << (32 - PrefixLength))`
- Валидировать `0 <= PrefixLength <= 32`
- В `RouteTable.AddOrUpdate`: аналогичная валидация

### 3.5 Longest-prefix-match
**Файл:** `RouteTable.cs`
**Проблема:** Нет метода `Lookup(uint address)`. Только exact match.
**Исправление:**
- Добавить `Route? Lookup(uint address)` — перебор всех маршрутов, выбор наиболее специфичного
- Опционально: Patricia trie для производительности (но для начала линейный поиск)

---

## Приоритет 4: Конфигурация

### 4.1 Валидация RouterId
**Файл:** `BgpConfig.cs:11-12`
**Проблема:** Default `"0.0.0.0"` нарушает RFC 4271 §6.8.
**Исправление:**
- Сделать `RouterId` `required` (в YAML)
- В `GetRouterIdAddress()` валидировать ≠ `0.0.0.0` и ≠ IPv6
- Бросать `InvalidOperationException` при невалидном значении

### 4.2 Валидация HoldTime/KeepAlive
**Файл:** `BgpConfig.cs:14-18`
**Проблема:** Любой int принимается. RFC: 0 или ≥3.
**Исправление:**
- `if (HoldTime != 0 && HoldTime < 3) throw`
- `if (KeepAlive != 0 && KeepAlive < 1) throw`
- Cross-check: `KeepAlive <= HoldTime / 3`

### 4.3 Валидация ApiPort
**Файл:** `AppConfig.cs:13-14`
**Проблема:** Default `5001`, документация говорит `5000`. Нет range-валидации.
**Исправление:**
- Изменить default на `5000` (согласовать с документацией)
- Валидировать `1 <= ApiPort <= 65535`

### 4.4 Валидация PeerConfig.Address
**Файл:** `PeerConfig.cs:9`
**Проблема:** Default `"0.0.0.0"` невалиден как peer address.
**Исправление:**
- Сделать `Address` `required` (в YAML)
- Валидировать `IPAddress.TryParse` при загрузке конфига

### 4.5 Post-deserialization валидация
**Файл:** `ConfigLoader.cs:14,18`
**Проблема:** После десериализации нет проверок.
**Исправление:**
- Добавить `AppConfig.Validate()` метод
- Вызывать из `Load`/`LoadFromText` после десериализации
- Бросать `InvalidOperationException` с описанием поля и файла

### 4.6 Strict YAML parsing
**Файл:** `ConfigLoader.cs:8`
**Проблема:** `IgnoreUnmatchedProperties` тихо глотает опечатки.
**Исправление:**
- Убрать `IgnoreUnmatchedProperties()` или добавить опцию strict mode
- Минимум: логировать предупреждения при обнаружении неизвестных свойств

### 4.7 Атомарное сохранение конфига
**Файл:** `ConfigLoader.cs:23-24`
**Проблема:** `File.WriteAllText` — неатомарно. Краш при записи = повреждённый файл.
**Исправление:**
- Запись в `.tmp` → `File.Move` (с заменой)
- Или: `File.WriteAllText(path + ".tmp", ...)` + `File.Replace(...)`

### 4.8 `global.json` rollback policy
**Файл:** `global.json:4-5`
**Проблема:** `rollForward: latestMajor` + `allowPrerelease: true` — пиннинг бессмысленен.
**Исправление:**
- `rollForward: latestFeature` (или пиннинг feature band `"10.0.*"`)
- `allowPrerelease: false`

---

## Приоритет 5: Тесты

### 5.1 Добавить ссылки на недостающие проекты
**Файл:** `BGPLite.Tests.csproj`
**Проблема:** Нет ссылок на `BGPLite.Api`, `BGPLite.Providers`, `BGPLite`.
**Исправление:**
- Добавить `ProjectReference` на все проекты решения

### 5.2 Негативные тесты парсера
**Проблема:** 8 критических путей без тестов.
**Исправление — добавить:**
- `ReadMessage_UnknownType_Throws`
- `ReadMessage_InvalidLength_Throws`
- `ReadMessage_Incomplete_Throws`
- `ReadMessage_OpenTooShort_Throws`
- `ReadMessage_UnsupportedVersion_Throws`
- `ReadMessage_UpdateTooShort_Throws`
- `ReadMessage_NotificationTooShort_Throws`
- `ReadMessage_UpdateMissingAttributes_Throws`

### 5.3 Тесты `PeerCommunityFilter`
**Проблема:** Нулевое покрытие непростой логики.
**Исправление — добавить:**
- `AcceptOutgoing_RouteWithAllowedCommunity_ReturnsTrue`
- `AcceptOutgoing_RouteWithoutCommunity_DeniedWhenFilterActive`
- `AcceptOutgoing_RouteWithNoOverlap_ReturnsFalse`
- `AcceptOutgoing_EmptyAllowedSet_AllRoutesPass`

### 5.4 Граничные значения `PrefixCodec`
**Проблема:** Только byte-aligned длины.
**Исправление — добавить:**
- Длины 1, 7, 9, 23, 25, 31
- Тест с host bits set (проверка masking)
- Тест `length > 32` → exception
- Тест с пустым буфером

### 5.5 Расширение тестов `BgpMessageTests`
**Исправление — добавить:**
- Roundtrip для 2-byte ASN paths
- Extended length attribute (>255 bytes)
- OPEN с пустым capabilities list
- Проверка порядка attributes в UPDATE
- Проверка capability data content (не только count)

### 5.6 Тесты `ConfigLoader.Save`
**Исправление — добавить:**
- `SaveLoad_Roundtrip_PreservesAllFields`
- `Load_InvalidYaml_Throws`
- `Load_MissingFile_Throws`
- `LoadFromText_EmptyBgpSection_UsesDefaults`

### 5.7 Тесты `BgpServer` и `BgpSession`
**Исправление — добавить:**
- Интеграционные тесты с mock TCP (minimal session lifecycle)
- Тест Hold Timer expiry → NOTIFICATION
- Тест NOTIFICATION перед socket close
- Тест concurrent send safety (если возможно)

---

## Приоритет 6: Архитектурные улучшения

### 6.1 Реализовать `BgpTimers`
**Проблема:** Класс существует, но мёртвый.
**Исправление:**
- Hold Timer, ConnectRetry Timer, Keepalive Timer
- Интеграция в `BgpSession.RunEstablishedAsync`

### 6.2 FSM: добавить состояние `Active`
**Проблема:** RFC 4271 §8 требует 6 состояний.
**Исправление:**
- Добавить `BgpFsmState.Active`
- Реализовать транзитивы: Connect → Active (при connect failure), Active → Connect (connect retry)

### 6.3 Async PeerStore
**Проблема:** `IPeerStore` полностью синхронный, блокирует session thread.
**Исправление:**
- Добавить async версии методов (`GetPeerByIpAsync`, etc.)
- Или: `ValueTask` возвраты

### 6.4 Кэширование community-запросов
**Проблема:** `PeerCommunityFilter` делает DB-запрос на каждый (route, peer).
**Исправление:**
- In-memory кэш с TTL на уровне сессии
- Инвалидация при обновлении subscriptions

### 6.5 Добавить iBGP/eBGP различение
**Проблема:** Нет типа пира в конфигурации.
**Исправление:**
- `PeerConfig.PeerType: IBGP | EBGP`
- Влияет на next-hop handling и AS_PATH prepend

### 6.6 MaxPrefix limit
**Проблема:** Нет ограничения количества префиксов на пира.
**Исправление:**
- `PeerConfig.MaxPrefix: uint?`
- При превышении → NOTIFICATION (Cease / Max Prefixes Exceeded)

---

## Порядок реализации

| Этап | Приоритет | Описание |
|------|-----------|----------|
| 1 | P1 | Гонки сессий, send lock, hold timer, NOTIFICATION, volatile state |
| 2 | P2 | Протокольная валидация (attributes, prefix, OPEN, AS_PATH) |
| 3 | P3 | Community filter, Route immutability, prefix masking, LPM |
| 4 | P4 | Конфигурация (validation, strict YAML, atomic save, global.json) |
| 5 | P5 | Тесты (negative paths, PeerCommunityFilter, PrefixCodec, integration) |
| 6 | P6 | Архитектура (BgpTimers, FSM Active, async PeerStore, cache) |

---

## Зависимости между задачами

```
1.1 (гонка сессий) ──┐
1.2 (send lock)    ──┤── 2.1 (порядок attributes)
1.3 (hold timer)   ──┤── 5.7 (интеграционные тесты)
1.4 (NOTIFICATION) ──┤
1.5 (volatile)     ──┘

2.2 (PrefixCodec)  ──┐
2.3 (OPEN encoding) ─┤── 5.4 (граничные тесты)
2.4 (ReadAsPath)    ─┤── 5.2 (негативные тесты)
2.5 (attribute len) ─┘

3.1 (community filter) ──┐
3.2 (Route immutable)  ──┤── 5.3 (тесты фильтра)
3.3 (AddOrUpdate)      ──┤
3.4 (prefix masking)   ──┤
3.5 (LPM)              ──┘

4.1-4.5 (config) ── 5.6 (тесты конфига)
```
