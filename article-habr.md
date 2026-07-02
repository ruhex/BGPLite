# Как я написал BGP-сервер и не сошёл с ума

*BGPLite — open-source BGP route-server на C# и .NET 10 примерно из 2500 строк кода. Он умеет принимать BGP-сессии, динамически загружать префиксы через RIPE Stat и управляться через HTTP API. Исходный код доступен на [GitHub](https://github.com/ruhex/BGPLite).*

Когда я впервые открыл [RFC 4271](https://datatracker.ietf.org/doc/html/rfc4271), мне казалось, что BGP — это какая-то чёрная магия из мира операторов связи. Через несколько недель я уже отлаживал собственный BGP-сервер, принимал реальные пиринги и спорил с MikroTik о 4-байтных ASN.

В этой статье расскажу, как появился BGPLite, какие грабли встретились по пути и почему написать собственный BGP-сервер оказалось гораздо проще, чем кажется на первый взгляд.

## Содержание

1. [Зачем всё это](#зачем-всё-это)
2. [Почему не BIRD, FRR и GoBGP](#почему-не-bird-frr-и-gobgp)
3. [Почему .NET](#почему-net)
4. [Как устроен BGP (кратко)](#как-устроен-bgp-кратко)
5. [Архитектура BGPLite](#архитектура-bgplite)
6. [Самые интересные технические проблемы](#самые-интересные-технические-проблемы)
   - [TCP-фрагментация](#tcp-фрагментация-почему-первый-прототип-сломался-в-реальной-сети)
   - [ASN32 и два дня дебага](#asn32-два-дня-дебага)
   - [Capability negotiation и Mikrotik](#capability-negotiation-почему-mikrotik-разрывал-сессию)
   - [Гонка при записи в сокет](#гонка-при-записи-в-сокет-первый-баг-в-проде)
   - [UPDATE-батчинг](#update-батчинг-когда-8000-префиксов-не-влезают-в-один-пакет)
   - [RIPE Stat caching](#ripe-stat-caching-50-пиров--1-запрос)
7. [Это уже работает](#это-уже-работает)
8. [Чего пока нет](#чего-пока-нет)
9. [Что дальше](#что-дальше)

---

<anchor>зачем-всё-это</anchor>

## Зачем всё это

Начну с честного признания: я хотел понять BGP изнутри.

Когда работаешь с сетями, BGP выглядит чем-то монструозным: RFC на десятки страниц, множество дополнительных спецификаций, сложные конфигурации в BIRD, FRR, Cisco и Juniper. Настраиваешь neighbor X.X.X.X remote-as 65444, и всё работает, но что именно происходит под капотом — часто остаётся загадкой.

Какие сообщения обмениваются маршрутизаторы? Как происходит согласование возможностей? Почему иногда сессия не поднимается? Что скрывается за всеми этими OPEN, KEEPALIVE и UPDATE пакетами?

Чтобы разобраться в этом не по документации, а на практике, я решил написать собственную реализацию BGP.

Параллельно у меня была конкретная практическая задача: нужен был route-server, который динамически раздаёт префиксы клиентам. Не full BGP router, не policy engine на миллионы маршрутов — просто «подключился по BGP, получил свои маршруты, работай». Причём набор маршрутов у каждого клиента свой: один хочет Cloudflare + Google, другой — префиксы определённой страны, третий — кастомный набор. И всё это должно управляться через HTTP API, потому что добавление клиента через CLI в 2026 году — это прошлый век.

<anchor>почему-не-bird-frr-и-gobgp</anchor>

## Почему не BIRD, FRR и GoBGP

Я посмотрел на имеющиеся инструменты:

- **BIRD** — прекрасный демон, но его конфиг — отдельный язык программирования. Хочешь добавить клиента через API? Генерируй конфиг, скармливай `birdc configure`, молись что парсер не подавился. Динамическая загрузка префиксов из внешних источников? Нет, из коробки.
- **FRR** (бывший Quagga) — тяжёлый, с legacy-архитектурой. Для простой задачи — overengineering.
- **GoBGP** — мощный, но огромный. Разобраться в кодовой базе сложнее, чем написать своё.

Общая проблема: все они — *full BGP speakers*. Они умеют всё (policy routing, route reflection, MPLS, EVPN), но для моей задачи это 90% лишнего.

И тут я подумал: а что если написать свой? Во-первых, это лучший способ понять протокол — реализовать его с нуля. Во-вторых, .NET 10 с его `Span<byte>`, async/await и EF Core — отличная платформа для такого проекта. В-третьих, ~2500 строк кода — это посильная задача на выходные.

Спойлер: потребовалось не один выходной. Но протокол действительно оказался проще, чем я ожидал.

<anchor>почему-net</anchor>

## Почему .NET

Спойлер: не потому что «модный». На выбор повлияли факторы:

**`Span<byte>` и `Memory<byte>`.** BGP — бинарный протокол. Парсинг пакетов на `ReadOnlySpan<byte>` без промежуточных аллокаций — это именно то, что нужно для сетевого кода. В Go пришлось бы возиться с `io.Reader` и копированиями, в Rust — бороться с borrow checker'ом (хотя там это было бы идиоматичнее). В .NET это выглядит естественно.

**Async I/O из коробки.** Каждая BGP-сессия — это две параллельные корутины (чтение и keepalive). `async/await` + `CancellationToken` — стандартный паттерн, без коллбэков и state machine вручную.

**EF Core для хранилища.** Пиры, подписки, кастомные префиксы — классическая реляционная модель. `DbContextFactory` + SQLite — три строки конфигурации, и у вас есть потокобезопасное хранилище.

**Ну и честно — я просто C#-разработчик.** Можно было бы написать на Go, Rust или C. Но зачем, когда ты знаешь свой инструмент и можешь на нём сделать всё то же самое? Лучший язык для проекта — тот, который ты знаешь.

---

<anchor>как-устроен-bgp-кратко</anchor>

## Как устроен BGP (кратко)

Если вы никогда не заглядывали под капот BGP — вот всё, что нужно знать для понимания статьи.

BGP имеет ровно **4 типа сообщений**:

| Тип | Код | Назначение |
|-----|-----|------------|
| OPEN | 1 | Рукопожатие: ASN, Hold Time, capabilities |
| UPDATE | 2 | Анонс/отзыв префиксов с атрибутами |
| NOTIFICATION | 3 | Сообщение об ошибке, после него сессия закрывается |
| KEEPALIVE | 4 | «Я жив», подтверждение OPEN |

Каждое сообщение начинается с 19-байтового заголовка: 16 байт маркера (`0xFF`), 2 байта длины, 1 байт типа. Формат — жёстко бинарный, без текстовых полей.

**Установка сессии** — это конечный автомат (FSM). Для route-server сценария достаточно минимального набора состояний:

```
Idle → Connect → OpenSent → OpenConfirm → Established
```

В Established сессия живёт: обе стороны обмениваются UPDATE (маршруты) и KEEPALIVE (подтверждение жизни). Если HOLD Timer истёк или пришёл NOTIFICATION — сессия закрывается.

**UPDATE** — самое сложное сообщение. Содержит три секции:
- Withdrawn Routes — префиксы, которые пир отзывает
- Path Attributes — метаданные маршрутов (Origin, AS_PATH, Next Hop, Communities)
- NLRI (Network Layer Reachability Information) — анонсируемые префиксы

Префиксы кодируются компактно: только значащие байты. `192.168.0.0/16` — это 3 байта, а не 5. `/24` — 4 байта. `/0` — 1 байт (только длина).

Всё остальное (capabilities, communities, MP-BGP) — опциональные расширения поверх этого базиса. Базовый BGP поразительно прост.

---

<anchor>архитектура-bgplite</anchor>

## Архитектура BGPLite

```
  Клиент (BIRD/Cisco/Mikrotik)
         │
         │ BGP (TCP :179)
         ▼
┌─────────────────┐    ┌──────────────────┐
│   BGP Server    │    │    HTTP API      │
│  (BgpSession)   │    │  (:5000)         │
│  (BgpMetrics)   │    │                  │
└────────┬────────┘    └────────┬─────────┘
         │                      │
         ▼                      ▼
┌─────────────────────────────────────────┐
│              Peer Store                 │
│         (EF Core + SQLite)              │
│  Peer → Subscription → CustomPrefix     │
│  Peer → Community                       │
└────────────────┬────────────────────────┘
                 │
         ┌───────┴───────┐
         ▼               ▼
┌────────────────┐ ┌──────────────┐
│  Route Table   │ │  Prefix      │
│  (Community    │ │  Service     │
│   Filters)     │ │  (Cache)     │
└────────────────┘ └──────┬───────┘
                          │
                   ┌──────┴───────┐
                   ▼              ▼
            ┌────────────┐ ┌───────────┐
            │ RIPE Stat  │ │ nets.txt  │
            │ API        │ │ (local)   │
            └────────────┘ └───────────┘
```

Проект разбит на 7 модулей по предметной области:

```
BGPLite/               # Entry point, DI, host setup
BGPLite.Protocol/      # Кодирование/декодирование BGP-сообщений (0 зависимостей)
BGPLite.Server/        # TCP listener, BGP session FSM
BGPLite.Routing/       # Route table, community filters
BGPLite.Providers/     # RIPE Stat клиент, кеш, локальные префиксы
BGPLite.Api/           # HTTP API, EF Core, PeerStore
BGPLite.Configuration/ # YAML-конфиг
```

`Protocol` не знает про сеть. `Server` не знает про базу. `Routing` не знает про HTTP. Чистый modular monolith без овер-инжиниринга.

Модель данных пира:

```cs
public class Peer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Ip { get; set; } = "";
    public uint? Asn { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "inactive";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSessionAt { get; set; }

    public List<PeerCommunity> Communities { get; set; } = [];
    public List<PeerSubscription> Subscriptions { get; set; } = [];
    public List<PeerCustomPrefix> CustomPrefixes { get; set; } = [];
}
```

Когда пир подключается по BGP, сервер ищет его в этой таблице по IP-адресу — и решает, какие маршруты отдать. У пира три вида связей: community-фильтры (какие маршруты получать), подписки на AS-листы (откуда брать) и кастомные префиксы (дополнительные).

---

<anchor>самые-интересные-технические-проблемы</anchor>

## Самые интересные технические проблемы

<anchor>tcp-фрагментация-почему-первый-прототип-сломался-в-реальной-сети</anchor>

### TCP-фрагментация: почему первый прототип сломался в реальной сети

Первый прототип я написал за вечер. Открыл TCP-сокет, читаю данные через `NetworkStream.ReadAsync()`, парсю BGP-сообщение. С BIRD на localhost — всё работает идеально.

Потом подключил реальный маршрутизатор через сеть. И всё сломалось.

`ReadAsync` не гарантирует, что вы прочитаете ровно один BGP-пакет. TCP — потоковый протокол. Один вызов `ReadAsync` может вернуть:
- Половину сообщения (TCP-сегмент пришёл не целиком)
- Полтора сообщения (два пакета склеились в буфере)
- 0 байт (соединение закрыто)

Мой наивный код:

```cs
// НЕ ДЕЛАЙТЕ ТАК
var buffer = new byte[4096];
var read = await stream.ReadAsync(buffer, ct);
var message = BgpMessageReader.ReadMessage(buffer.AsSpan(0, read));
```

Это работало на localhost, потому что там пакеты почти никогда не фрагментируются. Но через реальную сеть — случайные падения парсера с загадочными ошибками «Invalid BGP marker» или «Message too short».

Пришлось написать `ReadExactAsync`, который гарантированно читает заданное количество байт:

```cs
private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
{
    var totalRead = 0;
    while (totalRead < buffer.Length)
    {
        var read = await _stream.ReadAsync(buffer[totalRead..], ct);
        if (read == 0)
            throw new IOException("Connection closed by peer");
        totalRead += read;
    }
}
```

И читать в два приёма: сначала 19 байт заголовка (чтобы узнать полную длину), затем оставшуюся часть. Здесь же пригодился `ArrayPool<byte>` — вместо `new byte[4096]` на каждое сообщение мы арендуем буфер из пула. При десятках параллельных сессий разница в GC pressure заметна.

<anchor>asn32-два-дня-дебага</anchor>

### ASN32: два дня дебага

Я думал, что ASN — это просто 16-битное число в OPEN-пакете. Первые тесты с BIRD (ASN 65444) прошли успешно. Потом попробовал подключить клиента с ASN 397143 — сессия не установилась.

Два дня я искал проблему. Парсинг верный, байты правильные, но пир получает какой-то другой ASN. Оказалось, что поле ASN в OPEN-пакете — 16 бит, а в современном интернете используются 4-байтные ASN (AS > 65535). Значения > 65535 просто не помещаются.

Решение описано в [RFC 4893](https://datatracker.ietf.org/doc/html/rfc4893): capability с кодом 65 (Four-Octet ASN), а в 16-битном поле ставится `AS_TRANS` (23456) — reserved-значение, которое означает «смотрите в capabilities»:

```cs
var asn16 = _bgpConfig.Asn > ushort.MaxValue ? (ushort)23456 : (ushort)_bgpConfig.Asn;
```

При чтении нужно делать наоборот: сначала смотреть в capabilities, и только если capability 65 отсутствует — брать 16-битное поле. Это классический backward-compatible паттерн из RFC.

Но на этом история не закончилась. Я реализовал отправку 4-байтного ASN в своём OPEN, но забыл его *парсить* при чтении OPEN от пира. Клиент с ASN 397143 подключался, а сервер видел у него ASN 23456. Итог — ещё полдня дебага.

<anchor>capability-negotiation-почему-mikrotik-разрывал-сессию</anchor>

### Capability negotiation: почему Mikrotik разрывал сессию

Когда у тебя один тип клиента — BIRD на Linux — всё работает. Но в реальном мире к route server'у подключаются разные устройства: BIRD, FRR, Mikrotik RouterOS, Cisco IOS, Juniper JunOS. И у каждого свой «диалект» BGP.

Первая проблема возникла с Mikrotik. Сессия не поднималась — Mikrotik отправлял NOTIFICATION с ошибкой сразу после моего OPEN. Дамп пакетов показал: Mikrotik не отправлял capability MP-BGP (Multiprotocol Extensions), а мой сервер в ответ шлём ему MP-BGP IPv4/Unicast. Mikrotik это не ожидал и разрывал сессию.

Пришлось реализовать адаптацию capabilities: наш OPEN содержит только те capabilities, которые поддерживает пир:

```cs
var capabilities = new List<BgpCapabilityInfo>
{
    BgpCapabilityInfo.FourOctetAsn(_bgpConfig.Asn)
};

// Только если пир поддерживает MP IPv4/Unicast
if (PeerHasMpIpv4Unicast(remoteOpen.Capabilities))
    capabilities.Add(BgpCapabilityInfo.MultiprotocolIpv4Unicast());

// Только если пир поддерживает Route Refresh
if (remoteOpen.Capabilities.Any(c => c.Code == BgpConstants.Capability.RouteRefresh))
    capabilities.Add(BgpCapabilityInfo.RouteRefresh());
```

Мораль: capability negotiation — это не опциональная фича, а критически важная часть BGP. Без неё реальные устройства откажутся устанавливать сессию.

<anchor>гонка-при-записи-в-сокет-первый-баг-в-проде</anchor>

### Гонка при записи в сокет: первый баг в проде

У каждой BGP-сессии две параллельные задачи: `ReadLoopAsync` (чтение) и `KeepAliveLoopAsync` (отправка keepalive). Когда я добавил `RefreshRoutesAsync` — метод для обновления маршрутов при изменении подписок через API — две записи в сокет начали выполняться одновременно.

Два `WriteAsync` на один `NetworkStream` в один момент времени. TCP-буфер содержит куски двух BGP-сообщений, склеенные вместе. Пир получает мусор и закрывает сессию.

BGP — не framing-протокол. Нет разделителей между сообщениями. Длина определяется из заголовка, и если байты двух сообщений перемешались — пир не сможет распарсить ни одно.

Решение — `SemaphoreSlim(1, 1)` для сериализации всех записей:

```cs
private readonly SemaphoreSlim _sendLock = new(1, 1);

public async Task RefreshRoutesAsync()
{
    if (!IsEstablished) return;

    await _sendLock.WaitAsync();
    try
    {
        await WithdrawAllAsync();
        await SendAllRoutesAsync();
    }
    finally
    {
        _sendLock.Release();
    }
}
```

Почему не `lock`? Потому что `lock` не работает с `async`. Почему не `Monitor.Enter`? Та же причина. `SemaphoreSlim` — единственный примитив в .NET, который корректно работает с async/await.

<anchor>update-батчинг-когда-8000-префиксов-не-влезают-в-один-пакет</anchor>

### UPDATE-батчинг: когда 8000 префиксов не влезают в один пакет

Когда первый клиент подписался на большой список префиксов, сервер попытался отправить ~8000 префиксов одним UPDATE-сообщением. Пир молча игнорировал это сообщение — и клиент не получал ни одного маршрута.

Максимальный размер BGP-сообщения — 4096 байт ([RFC 4271, §4.1](https://datatracker.ietf.org/doc/html/rfc4271#section-4.1)). В один UPDATE влезает ~120-130 префиксов, в зависимости от размера path attributes.

Решение — батчинг по 100:

```cs
const int maxNlriPerUpdate = 100;
foreach (var route in routes)
{
    batch.Add(route);
    if (batch.Count >= maxNlriPerUpdate)
    {
        await SendRouteBatchAsync(nextHop, batch);
        batch.Clear();
    }
}
```

Каждый батч — отдельный UPDATE с полным набором path attributes (Origin, AS_PATH, Next Hop). Это избыточно (повторяем атрибуты), но гарантирует, что каждое сообщение валидно само по себе.

<anchor>ripe-stat-caching-50-пиров--1-запрос</anchor>

### RIPE Stat caching: 50 пиров → 1 запрос

RIPE NCC предоставляет бесплатный API, который отдаёт префиксы, анонсируемые конкретным ASN:

```
GET https://stat.ripe.net/data/ris-prefixes/data.json?resource=AS13335&list_prefixes=true
```

Проблема: у RIPE есть rate limits, а у крупных AS'ок (Cloudflare, Google) — сотни и тысячи префиксов. Если 50 пиров подписаны на Cloudflare, нельзя делать 50 запросов к RIPE Stat.

Решение — кеш на 1 час в `ConcurrentDictionary`:

```cs
private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint, byte)> Data, DateTime CachedAt)> _cache = new();
private readonly TimeSpan _cacheTtl = TimeSpan.FromHours(1);
```

Первый запрос к AS13335 идёт в RIPE Stat, последующие 49 — берутся из кеша. Через час — обновление при следующем обращении.

Для локальных префиксов (например, сети конкретной страны) используется файл `nets.txt` — загружается один раз при старте, без внешних запросов.

---

<anchor>это-уже-работает</anchor>

## Это уже работает

Проект уже развёрнут на реальном сервере и обслуживает несколько BGP-пиров: **[bgp.vhex.dev](https://bgp.vhex.dev/)**

### HTTP API

API реализован на `HttpListener` — без ASP.NET Core. Зачем тянуть Kestrel, когда нужен роутинг на 10 эндпоинтов?

Фишка `GET /api/server` — генерация готовых конфигов для BIRD, Cisco IOS и Mikrotik RouterOS с подставленными ASN и Router ID. Клиенту остаётся скопипастить и подставить свой IP:

```json
{
  "bird": [
    "protocol bgp bgplite {",
    "  local as <YOUR_ASN>;",
    "  neighbor 10.0.0.1 as 65444;",
    "  multihop;",
    "  hold time 180;",
    "  ipv4 {",
    "    import filter bgplite_in;",
    "    export none;",
    "  };",
    "}"
  ]
}
```

Исходный код: [github.com/ruhex/BGPLite](https://github.com/ruhex/BGPLite).

---

<anchor>чего-пока-нет</anchor>

## Чего пока нет

Буду честен — BGPLite не претендует на замену BIRD или FRR. Пока не реализовано:

- **IPv6 / MP-BGP** — только IPv4 unicast
- **RPKI-валидация** — префиксы не проверяются на валидность
- **BGP MD5 Authentication** — нет защиты сессии от подделки
- **Route Refresh** — обновление префиксов требует отзыва и повторной отправки всех маршрутов
- **Graceful Restart** — при падении сервера пиры теряют все маршруты
- **BFD** — нет быстрого обнаружения обрыва связи (только BGP keepalive)
- **BMP** — нет мониторинга BGP-сессий
- **Full Table** — не предназначен для приёма полных таблиц интернет-маршрутов (~900K префиксов)

Это осознанные ограничения. BGPLite решает конкретную задачу — персональная раздача префиксов по подпискам. Для full BGP routing используйте BIRD или FRR. Впрочем, некоторые пункты из этого списка появятся в будущих версиях — зависит от интереса сообщества.

---

<anchor>что-дальше</anchor>

## Что дальше

BGPLite будет активно развиваться, если проект будет интересен сообществу. Приоритеты:
- Route Refresh (soft reconfig без разрыва сессии)
- RPKI-валидация префиксов
- MP-BGP для IPv6
- Вынос `BGPLite.Protocol` в отдельный NuGet-пакет — чтобы реализацию BGP-протокола можно было переиспользовать в других проектах без привязки к route-server'у
- Больше источников данных: помимо RIPE Stat добавить Hurricane Electric (HE) BGP Toolkit и другие
- Исключающие правила для префиксов — возможность указать префиксы, которые не нужно получать, даже если они есть в подписках
- Prometheus/Grafana метрики
- Web UI для управления пирами

В наше время развитие сетевых технологий важнее, чем когда-либо. CDN, edge computing, multi-cloud, географически распределённые системы — всё это требует понимания маршрутизации. BGP — не магия вендоров, а понятный протокол, который можно реализовать за выходные.

Если вам интересна тема — присоединяйтесь: [github.com/ruhex/BGPLite](https://github.com/ruhex/BGPLite). Звёздочки, issue и pull request'ы приветствуются.
