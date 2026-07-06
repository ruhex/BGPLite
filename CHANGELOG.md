# Changelog

## [1.4.4](https://github.com/ruhex/BGPLite/compare/v1.4.3...v1.4.4) (2026-07-06)


### Bug Fixes

* **protocol:** Graceful Restart R bit false on fresh session + correct restartTime ([#203](https://github.com/ruhex/BGPLite/issues/203)) ([b716350](https://github.com/ruhex/BGPLite/commit/b71635044241302af7222c151cabc18ea8ff2886))

## [1.4.3](https://github.com/ruhex/BGPLite/compare/v1.4.2...v1.4.3) (2026-07-06)


### Bug Fixes

* **server:** refresh only the matching (Ip, Asn) session on shared IPs ([#200](https://github.com/ruhex/BGPLite/issues/200)) ([9141285](https://github.com/ruhex/BGPLite/commit/91412850caa6b6b023ff3eaba6281c320adee97e))

## [1.4.2](https://github.com/ruhex/BGPLite/compare/v1.4.1...v1.4.2) (2026-07-06)


### Bug Fixes

* **api:** trigger peer refresh on source add/delete/toggle ([#197](https://github.com/ruhex/BGPLite/issues/197)) ([139c150](https://github.com/ruhex/BGPLite/commit/139c150e3955ec18688080be9621c3a354687a1c))

## [1.4.1](https://github.com/ruhex/BGPLite/compare/v1.4.0...v1.4.1) (2026-07-06)


### Bug Fixes

* **api:** map 0.0.0.0 and :: to + wildcard for HttpListener on Linux ([#195](https://github.com/ruhex/BGPLite/issues/195)) ([4ac0979](https://github.com/ruhex/BGPLite/commit/4ac09790c63175039de768554b308c6c90555b41))
* **api:** map 0.0.0.0 to + for HttpListener on Linux ([58bd106](https://github.com/ruhex/BGPLite/commit/58bd1068719942b3a2a414750588a58dafab60e8))

## [1.4.0](https://github.com/ruhex/BGPLite/compare/v1.3.0...v1.4.0) (2026-07-06)


### Features

* **api:** PeerCustomSource entity + PeerStore CRUD + REST API ([#146](https://github.com/ruhex/BGPLite/issues/146)) ([42662c5](https://github.com/ruhex/BGPLite/commit/42662c5a33d51ffd60ac9782e4883079587732a5))
* **server:** fetch and advertise per-peer user URL sources ([#147](https://github.com/ruhex/BGPLite/issues/147)) ([#149](https://github.com/ruhex/BGPLite/issues/149)) ([0bb045a](https://github.com/ruhex/BGPLite/commit/0bb045a3796451d7f20d0b428d147b2bb3d8b2a5))


### Bug Fixes

* **api:** /api/me always returns peers array, disambiguate by ?asn= ([#23](https://github.com/ruhex/BGPLite/issues/23)) ([335d67f](https://github.com/ruhex/BGPLite/commit/335d67f1bf40d1de924e79e4203aa79b963fb44f))
* **api:** cap request body size — defend against OOM DoS ([#171](https://github.com/ruhex/BGPLite/issues/171)) ([85963b6](https://github.com/ruhex/BGPLite/commit/85963b6a509cdb9ab0286bf2e9ebb5df825fbba8))
* **api:** make ManagementApi.Dispose() idempotent ([#141](https://github.com/ruhex/BGPLite/issues/141)) ([7f862fe](https://github.com/ruhex/BGPLite/commit/7f862fe36c75aca839cce4132e8ef6b2b1ca699b))
* **api:** stop leaking raw exception messages in 500 responses ([#172](https://github.com/ruhex/BGPLite/issues/172)) ([bf0b204](https://github.com/ruhex/BGPLite/commit/bf0b204d1f601acaca80747a50b6f9fb82986e74))
* **protocol:** correct AGGREGATOR (6B) and AS4_AGGREGATOR (8B) lengths — regression of [#31](https://github.com/ruhex/BGPLite/issues/31) ([#169](https://github.com/ruhex/BGPLite/issues/169)) ([4519d13](https://github.com/ruhex/BGPLite/commit/4519d132efdcdb7b4e1ea9789cf8388048281594))
* **providers:** block IPv4-embedding IPv6 forms + restrict ports in SSRF defense ([#173](https://github.com/ruhex/BGPLite/issues/173)) ([96656bd](https://github.com/ruhex/BGPLite/commit/96656bd148c59ec1d173436c8a4c46e8ce082e47))
* **providers:** connect SSRF-validated hosts IPv4-first, fall through on failure ([#151](https://github.com/ruhex/BGPLite/issues/151)) ([#153](https://github.com/ruhex/BGPLite/issues/153)) ([6d98aed](https://github.com/ruhex/BGPLite/commit/6d98aedc58c77cf62c90140cc93ca2c685662c43))
* **providers:** reject /0 + /33+ and mask host bits in PrefixListParser ([#162](https://github.com/ruhex/BGPLite/issues/162)) ([873d339](https://github.com/ruhex/BGPLite/commit/873d339ccb7261875568b2a3ce86c7c6753cd370))
* **providers:** RIPEstat resilience — stale-on-failure, per-ASN gate, bounded cache ([#163](https://github.com/ruhex/BGPLite/issues/163), [#164](https://github.com/ruhex/BGPLite/issues/164), [#165](https://github.com/ruhex/BGPLite/issues/165)) ([6a5a6ea](https://github.com/ruhex/BGPLite/commit/6a5a6eafb211a51c69b1c6bf98607b9d5a73bb79))
* **providers:** send per-source headers/timeout per-request, not via client mutation ([#155](https://github.com/ruhex/BGPLite/issues/155)) ([#170](https://github.com/ruhex/BGPLite/issues/170)) ([ef71e3c](https://github.com/ruhex/BGPLite/commit/ef71e3cba768e1e0e40b2dc776a58954800fef67))
* **server:** cancel _advertisedPrefixesLock + add SendTimeout backstop ([#175](https://github.com/ruhex/BGPLite/issues/175)) ([255e51d](https://github.com/ruhex/BGPLite/commit/255e51d69a2467dcb880144d65361bf2e75e9982))
* **server:** honor CancellationToken in StopAsync and NotifyCeaseAsync ([#161](https://github.com/ruhex/BGPLite/issues/161)) ([a46c07f](https://github.com/ruhex/BGPLite/commit/a46c07f3e4ebf1607f94d01b3dd4c034a5aaec7c))
* **server:** make ConfigCommunityResolver._parsed thread-safe ([#159](https://github.com/ruhex/BGPLite/issues/159)) ([#174](https://github.com/ruhex/BGPLite/issues/174)) ([c987ea2](https://github.com/ruhex/BGPLite/commit/c987ea231613fcdd42f484f8f29a49b977a73659))
* **server:** serialize IpAcceptThrottle dict mutations with a coarse lock ([#133](https://github.com/ruhex/BGPLite/issues/133)) ([b8fa475](https://github.com/ruhex/BGPLite/commit/b8fa475f18df7fbc393339774288df1b9d99bf3b))


### Performance Improvements

* **api:** suppress MultipleCollectionIncludeWarning ([#138](https://github.com/ruhex/BGPLite/issues/138)) ([6d8a704](https://github.com/ruhex/BGPLite/commit/6d8a704ce9a3bedbef7238b51218e173ab91bb86))
* **providers:** URL-keyed TTL cache for per-peer user-source fetches ([#150](https://github.com/ruhex/BGPLite/issues/150)) ([#152](https://github.com/ruhex/BGPLite/issues/152)) ([7a3d742](https://github.com/ruhex/BGPLite/commit/7a3d742af0b97e638b561cc034c4ca5a474687cf))
* remaining hot-path allocation reductions ([#85](https://github.com/ruhex/BGPLite/issues/85)) ([a20b470](https://github.com/ruhex/BGPLite/commit/a20b4700aec702c41b56038d9e80086d3f576e5b))
* **routing:** replace GroupBy with manual partition in ExactUnionPrefixAggregator ([#82](https://github.com/ruhex/BGPLite/issues/82)) ([0c7e0cf](https://github.com/ruhex/BGPLite/commit/0c7e0cf9238bd60db97df3058c108a26de78d36e))

## [1.3.0](https://github.com/ruhex/BGPLite/compare/v1.2.0...v1.3.0) (2026-07-04)


### Features

* **api:** cap concurrent management-API requests ([#119](https://github.com/ruhex/BGPLite/issues/119)) ([71761b2](https://github.com/ruhex/BGPLite/commit/71761b2f7eb062c853c704b637da5d31ad1a47d2))
* **api:** per-client-IP token-bucket rate limiting ([#118](https://github.com/ruhex/BGPLite/issues/118)) ([3d35e9d](https://github.com/ruhex/BGPLite/commit/3d35e9db353c0b310ad97329dd8b0896f18e3708))
* **config:** hot-reload soft config without restarting the service ([#136](https://github.com/ruhex/BGPLite/issues/136)) ([bb722c9](https://github.com/ruhex/BGPLite/commit/bb722c9a16d12181b418963d5070b4561fe83c4e))
* **protocol:** add RFC 8092 Large Communities codec + wiring ([#35](https://github.com/ruhex/BGPLite/issues/35)) ([19abd15](https://github.com/ruhex/BGPLite/commit/19abd15a6840b6e3bf110a6d8b38d5474693e191))


### Bug Fixes

* **api:** enable SQLite WAL + busy_timeout for peer-store resilience ([#111](https://github.com/ruhex/BGPLite/issues/111)) ([8ca4c9c](https://github.com/ruhex/BGPLite/commit/8ca4c9cf6e7582a3725f9474fef16bb872314198))
* **api:** gate CORS on configurable origin allowlist ([#99](https://github.com/ruhex/BGPLite/issues/99)) ([1a66679](https://github.com/ruhex/BGPLite/commit/1a666792cd4ca6d3ea98dfcdf82ce9b9e8299a77))
* **api:** gate forwarding headers on trusted proxies ([#117](https://github.com/ruhex/BGPLite/issues/117)) ([3e0dae4](https://github.com/ruhex/BGPLite/commit/3e0dae4fddac6a678bf6c7b48abb6531e1a0da2d))
* **api:** make ManagementApi routing fully async ([#92](https://github.com/ruhex/BGPLite/issues/92)) ([#113](https://github.com/ruhex/BGPLite/issues/113)) ([5d419e8](https://github.com/ruhex/BGPLite/commit/5d419e8a867e97a7a2578833f29e64c1dc219b09))
* **api:** sanitize user input in logs + drop raw-body logging ([#120](https://github.com/ruhex/BGPLite/issues/120)) ([e875f1d](https://github.com/ruhex/BGPLite/commit/e875f1dc6cb7f4d112adddde6cd85cfbc0c0ba83))
* **api:** validate custom-prefix CIDRs + preserve on omit in peer update ([#100](https://github.com/ruhex/BGPLite/issues/100)) ([0676748](https://github.com/ruhex/BGPLite/commit/06767489a570cffa647a72ff8fb7ffd4289b83ad))
* Cease subcode (RFC 4486) + /api/asn-lists type by Kind ([#75](https://github.com/ruhex/BGPLite/issues/75)) ([fb960b7](https://github.com/ruhex/BGPLite/commit/fb960b7b2ca941fc82ca9bf32f5638fd5de5c749))
* **config:** strict YAML deserialization — unknown keys fail-loud ([#102](https://github.com/ruhex/BGPLite/issues/102)) ([43e60c2](https://github.com/ruhex/BGPLite/commit/43e60c2e62fc30011ddb2d03b3fbd2b77f1b773b))
* **config:** validate YAML at startup ([#89](https://github.com/ruhex/BGPLite/issues/89)) ([8dcde68](https://github.com/ruhex/BGPLite/commit/8dcde68017ef654fa65071a0dc41f0a37631ec69))
* **logging:** silence EF Core SQL spam + Docker log rotation ([#72](https://github.com/ruhex/BGPLite/issues/72)) ([#73](https://github.com/ruhex/BGPLite/issues/73)) ([0d06496](https://github.com/ruhex/BGPLite/commit/0d06496aa67f1f650abbfd24b76fe8e140927568))
* **providers:** thread CancellationToken through IPrefixService ([#114](https://github.com/ruhex/BGPLite/issues/114)) ([dd57479](https://github.com/ruhex/BGPLite/commit/dd57479147839d4627be137fb6f254671bf59d1d))
* reject IPv6 next hops and honor well-known communities ([#67](https://github.com/ruhex/BGPLite/issues/67)) ([d51e5d6](https://github.com/ruhex/BGPLite/commit/d51e5d648fab798117b3156dba3444a4e2a10e93))
* **server:** evict idle IPs from IpAcceptThrottle ([#115](https://github.com/ruhex/BGPLite/issues/115) follow-up) ([42ff0dd](https://github.com/ruhex/BGPLite/commit/42ff0dda22c4a0e977b6d5f673aef1472eb0f429))
* **server:** harden BGP listener against connection floods ([#115](https://github.com/ruhex/BGPLite/issues/115)) ([c9b7201](https://github.com/ruhex/BGPLite/commit/c9b72017ecf68eab3ebf37b0e44a54ebc5f68c2e))
* **server:** keep session up on a single malformed UPDATE ([#94](https://github.com/ruhex/BGPLite/issues/94)) ([#109](https://github.com/ruhex/BGPLite/issues/109)) ([7aa935c](https://github.com/ruhex/BGPLite/commit/7aa935c28ee94e18642a2598a59410c7686e07d7))


### Performance Improvements

* **api:** AsNoTracking on all PeerStore read paths ([#112](https://github.com/ruhex/BGPLite/issues/112)) ([ea05c93](https://github.com/ruhex/BGPLite/commit/ea05c93ad5962dd24a99ad9c1ca6eb85f47a93ad))
* **api:** collapse SendAllRoutesAsync peer loads into one roundtrip ([#84](https://github.com/ruhex/BGPLite/issues/84)) ([c6ffc43](https://github.com/ruhex/BGPLite/commit/c6ffc439331e2d7cdf5cd8b7f9fce20ff7867d0b))
* **providers:** parallelize PrefixService ASN resolution ([#83](https://github.com/ruhex/BGPLite/issues/83)) ([372284a](https://github.com/ruhex/BGPLite/commit/372284a840722f569a916e36d084c54edad12e6e))
* **routing:** resolve peer community allow-set once per send ([#106](https://github.com/ruhex/BGPLite/issues/106)) ([e3bec19](https://github.com/ruhex/BGPLite/commit/e3bec19297cf121c276115a8ee1d7bd5095ebe4a))
* **server:** cache UPDATE path attributes per community set ([#87](https://github.com/ruhex/BGPLite/issues/87)) ([e2977f5](https://github.com/ruhex/BGPLite/commit/e2977f588e35d2056a77201a5be5f5274b4acd04))
* **server:** short-circuit GroupByCommunitySet for single-set batches ([#86](https://github.com/ruhex/BGPLite/issues/86)) ([c23fa89](https://github.com/ruhex/BGPLite/commit/c23fa899896e5a205f9cd4717546741e31a132a3))

## [1.2.0](https://github.com/ruhex/BGPLite/compare/v1.1.0...v1.2.0) (2026-07-02)


### Features

* **providers:** AsnPrefixProvider — support Kind: asn in PrefixSources ([#68](https://github.com/ruhex/BGPLite/issues/68)) ([530ab70](https://github.com/ruhex/BGPLite/commit/530ab703b70c2a9d2dadee8d3dbc2aa6de90bdec))

## [1.1.0](https://github.com/ruhex/BGPLite/compare/v1.0.1...v1.1.0) (2026-07-02)


### Features

* **routing:** per-list BGP communities via ICommunityResolver (Phase 1 of [#63](https://github.com/ruhex/BGPLite/issues/63)) ([#64](https://github.com/ruhex/BGPLite/issues/64)) ([f3e3e1d](https://github.com/ruhex/BGPLite/commit/f3e3e1d687334564baf82f9730defdb9571c0899))
* **routing:** static communities for custom prefixes/AS + /api/community-scheme ([#66](https://github.com/ruhex/BGPLite/issues/66)) ([eb29aae](https://github.com/ruhex/BGPLite/commit/eb29aae50827b8715d93e36928980e0815325598))

## [1.0.1](https://github.com/ruhex/BGPLite/compare/v1.0.0...v1.0.1) (2026-07-02)


### Bug Fixes

* **deps:** bump EF Core Sqlite to 10.0.9 in Tests (NU1605 with [#54](https://github.com/ruhex/BGPLite/issues/54)) ([#58](https://github.com/ruhex/BGPLite/issues/58)) ([c495f11](https://github.com/ruhex/BGPLite/commit/c495f1173949fecb65c32303dfea8d67b7a5540f))

## 1.0.0 (2026-07-02)


### Features

* add `BgpNotificationException` for handling BGP protocol errors with RFC 4271 codes ([4612f66](https://github.com/ruhex/BGPLite/commit/4612f66906ff4d0b5e0935122f512b2efa0e8986))
* add `Enumerate` method to `RouteTable` for efficient route enumeration ([018d121](https://github.com/ruhex/BGPLite/commit/018d12195303ac8ec2df856cb7500c8de3f5d9c7))
* add `IPrefixAggregator` interface for route summarization ([61faf93](https://github.com/ruhex/BGPLite/commit/61faf93d3e9d711f824698f82b727185f44b61bc))
* add caching for RU prefix set in `GetRuPrefixesAsync` ([0367f83](https://github.com/ruhex/BGPLite/commit/0367f83af9c4fefd5c87dc166df2b0b23a17620b))
* add Cease notification for graceful shutdown (RFC 4271 §6.2) ([037096e](https://github.com/ruhex/BGPLite/commit/037096e3cd6fe42240c11db622ab6586916c90ba))
* add custom ASN support for peers ([f2e9d82](https://github.com/ruhex/BGPLite/commit/f2e9d820838bd9dd847f82c0b67ebe90ec9243e2))
* add Graceful Restart support compliant with RFC 4724 ([ef86910](https://github.com/ruhex/BGPLite/commit/ef86910a38ee8c116d6be6b5aa091a2603d5c206))
* add HTTP and file prefix providers with testing coverage ([531cc84](https://github.com/ruhex/BGPLite/commit/531cc84db1a5c59f614bd43e6f726e4bcd740a21))
* add prefix aggregation and community-aware route grouping ([251c79c](https://github.com/ruhex/BGPLite/commit/251c79cac2a74ec6a1030b69b29f803e20ba04a7))
* add prefix cache warm-up routine to `PrefixService` ([0a9300d](https://github.com/ruhex/BGPLite/commit/0a9300db5784102e2c9445222498a8f1fb814d11))
* add RU defaults and fallback logic for unconfigured/empty peers ([0d2cdc2](https://github.com/ruhex/BGPLite/commit/0d2cdc206db31feb96211ea19584224de5b6292f))
* add support for prefix-source subscriptions in `BgpSession` ([47e0fc4](https://github.com/ruhex/BGPLite/commit/47e0fc4c8272d183dc71b4f42db1cf434a2ba294))
* **configuration:** add Asn field to PrefixSourceConfig for AS-number scoping ([#25](https://github.com/ruhex/BGPLite/issues/25)) ([448de4e](https://github.com/ruhex/BGPLite/commit/448de4e5af0750d929f259f22fd9c535dc310012))
* enhance BGP configuration with custom filters and eBGP improvements ([3a3e0a8](https://github.com/ruhex/BGPLite/commit/3a3e0a872508d01622137f4d2c1bb86bdf224f99))
* enhance BGP session stability and compliance with RFC 4271 ([dfa286c](https://github.com/ruhex/BGPLite/commit/dfa286c415bcd3d22760f017aa8855f8b8e82b22))
* enhance session management, route handling, and peer operations ([a774418](https://github.com/ruhex/BGPLite/commit/a774418abd87bc8820770f919f0203ed55761165))
* extend PrefixService with local file support and RU-specific prefix handling, integrate with config and API ([b829ad7](https://github.com/ruhex/BGPLite/commit/b829ad71f55d216a68af0845816080ace3aa3eb1))
* implement PrefixService for cached prefix lookup and enhance ASN list handling across modules ([8077ae1](https://github.com/ruhex/BGPLite/commit/8077ae14c0791212ebf8109c9919c73cb2923a6f))
* improve logging and streamline peer creation/update logic ([5efc623](https://github.com/ruhex/BGPLite/commit/5efc62379203102e34f6e55ac939193b144db30e))
* improve logging for `RefreshPeerAsync` and handle missing/invalid sessions ([b25c3e9](https://github.com/ruhex/BGPLite/commit/b25c3e99f9ea448ab66d4c32483a82658406fd5c))
* improve session handling and peer status tracking ([f09e99e](https://github.com/ruhex/BGPLite/commit/f09e99e47be6c82397dd97f7e873bd22437a4381))
* initial commit — BGPLite BGP route server ([9a6c0af](https://github.com/ruhex/BGPLite/commit/9a6c0af00bb09b0f37552ed10c97444dbcf8221d))
* integrate RIPE Stat support for dynamic ASN-based prefix management and extend peer store capabilities ([a579f69](https://github.com/ruhex/BGPLite/commit/a579f691f8e131a8a9fd69329453bf05863eba5a))
* refactor to standardize service interfaces, add IPrefixService and IPeerStore, enable dynamic API port configuration and CORS support ([3fc62f0](https://github.com/ruhex/BGPLite/commit/3fc62f05b377ab048898525cc0ab3becf80ad271))
* update MikroTik BGP configuration to align with RouterOS v7 ([7f76077](https://github.com/ruhex/BGPLite/commit/7f760778f2016821e8dbb215948deb45c0eff35e))


### Bug Fixes

* **api:** key peer records by (Ip, Asn) so NAT'd peers don't share one row ([#21](https://github.com/ruhex/BGPLite/issues/21)) ([96e5b8d](https://github.com/ruhex/BGPLite/commit/96e5b8d42b870b9f07961856f050b9399d7b608f))
* configure RIPEstat timeout and add retry for heavy ris-prefixes … ([915741b](https://github.com/ruhex/BGPLite/commit/915741b27c61d43df1e9208ce01b11824d4d0280))
* configure RIPEstat timeout and add retry for heavy ris-prefixes queries ([287257f](https://github.com/ruhex/BGPLite/commit/287257f2db9dffbbef78c2a2a157344bc35d5b23))
* correct API route and table mapping for custom ASNs ([cba7a3c](https://github.com/ruhex/BGPLite/commit/cba7a3cdf65cbac97a704cc1e5c89475b792075a))
* only log session closure metric if previously established ([5eafe61](https://github.com/ruhex/BGPLite/commit/5eafe6155931b78db9fe76ba6ba4407e162539c1))
* prevent out-of-bounds reads in AS_PATH attribute parsing ([9709c69](https://github.com/ruhex/BGPLite/commit/9709c69f4829a6fea9fe7bff4185c4adfb9263e0))
* remove redundant route count check in `SendRoutesAsync` call ([2ab6062](https://github.com/ruhex/BGPLite/commit/2ab6062b584b51a85ec99eb92d6d6a8f74dc7378))
* **server:** harden session lifecycle and close Cease/silent-close teardown races ([09596ed](https://github.com/ruhex/BGPLite/commit/09596ed7974cc816672ef3a1b4d104b1bc74451e))
* **server:** harden session lifecycle, send lock, and Cease handling ([bd924df](https://github.com/ruhex/BGPLite/commit/bd924df127d668c155af6bcf5fee3165fb01e83d))
* **server:** harden shutdown teardown and dispose races ([5387dd3](https://github.com/ruhex/BGPLite/commit/5387dd3eb23b105d132415f4aa2a3349845c6d36))
* **server:** include remote port in session logs so same-IP peers are distinguishable ([#24](https://github.com/ruhex/BGPLite/issues/24)) ([941d6f0](https://github.com/ruhex/BGPLite/commit/941d6f06cc441077d6d54dbe0653487dfe54823c))
* **server:** key BGP sessions by TCP connection (remote IP + port), not remote IP ([#20](https://github.com/ruhex/BGPLite/issues/20)) ([554fb11](https://github.com/ruhex/BGPLite/commit/554fb11f677c69e5080d26008e1ab0d82c2df0f1))
* **server:** make session replacement atomic with TryUpdate; harden test reads ([c58dc8b](https://github.com/ruhex/BGPLite/commit/c58dc8b30bf1b46f3f01b4130981c31d798d4efa))
* **server:** move NotifyCeaseAsync CAS before send to close teardown race ([7ba07c7](https://github.com/ruhex/BGPLite/commit/7ba07c70b88effa07df8be85e84e55047f22067e))
* **server:** split teardown reasons and close race on Cease/silent-close ([968f2e4](https://github.com/ruhex/BGPLite/commit/968f2e4d0290e5f95370500ee6c8aad0c24b5e51))
