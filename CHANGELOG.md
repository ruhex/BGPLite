# Changelog

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
