# Changelog

## [1.6.0](https://github.com/ruhex/BGPLite/compare/v1.5.0...v1.6.0) (2026-09-04)


### Features

* **api,protocol:** IPv6 templates, per-AFI GR, IPv6 peer validation — phase 5 of [#14](https://github.com/ruhex/BGPLite/issues/14) ([#412](https://github.com/ruhex/BGPLite/issues/412)) ([0389d24](https://github.com/ruhex/BGPLite/commit/0389d24d03ac9d9aa692927e5c3dba87b32314f4))
* **protocol,server:** outbound MP_REACH + dual-stack prefix pipeline — phase 4b of [#14](https://github.com/ruhex/BGPLite/issues/14) ([#411](https://github.com/ruhex/BGPLite/issues/411)) ([f006a94](https://github.com/ruhex/BGPLite/commit/f006a94c1e3763371be1e76f0dfc76176797f41e))
* **protocol:** IPv6 dual-stack address model — phase 1 of [#14](https://github.com/ruhex/BGPLite/issues/14)/[#15](https://github.com/ruhex/BGPLite/issues/15) ([#401](https://github.com/ruhex/BGPLite/issues/401)) ([808a48b](https://github.com/ruhex/BGPLite/commit/808a48beb36d9dff02eedc844abd3720add68993))
* **protocol:** MP-BGP IPv6/Unicast — phase 2 of [#14](https://github.com/ruhex/BGPLite/issues/14)/[#15](https://github.com/ruhex/BGPLite/issues/15) ([#402](https://github.com/ruhex/BGPLite/issues/402)) ([3988098](https://github.com/ruhex/BGPLite/commit/39880985b1ac82eb0e103ad2f477735d3b70cf8f))
* **protocol:** RFC 7606 attribute discard — malformed AGGREGATOR/AS4_AGGREGATOR keeps the routes ([#306](https://github.com/ruhex/BGPLite/issues/306)) ([8d34ecf](https://github.com/ruhex/BGPLite/commit/8d34ecf653e58e632bf694d07b65eb5392195cba))
* **providers:** auto-refresh with ETag/conditional requests + periodic push ([#215](https://github.com/ruhex/BGPLite/issues/215)) ([92ab5e3](https://github.com/ruhex/BGPLite/commit/92ab5e3e5c369b47b18cf5f92756b247f8c61cea))
* **routing:** custom prefixes suppress the source prefixes they cover ([#220](https://github.com/ruhex/BGPLite/issues/220)) ([#385](https://github.com/ruhex/BGPLite/issues/385)) ([ac0f852](https://github.com/ruhex/BGPLite/commit/ac0f8521cc9df0f00865e1fa520a2e4a36ec479b))
* **routing:** IPv6 LPM + 128-bit family-aware aggregation — phase 3 of [#14](https://github.com/ruhex/BGPLite/issues/14) ([#405](https://github.com/ruhex/BGPLite/issues/405)) ([0583345](https://github.com/ruhex/BGPLite/commit/0583345b293d600d72e8c44b57ae654eb498df64))
* **server:** dual-mode IPv6+IPv4 BGP listener — phase 4a of [#14](https://github.com/ruhex/BGPLite/issues/14) ([#406](https://github.com/ruhex/BGPLite/issues/406)) ([52a1554](https://github.com/ruhex/BGPLite/commit/52a155417678d6b52fd47e56797e06492ea04cd8))
* **server:** optional per-peer TCP-MD5 authentication (RFC 2385) ([#36](https://github.com/ruhex/BGPLite/issues/36)) ([#395](https://github.com/ruhex/BGPLite/issues/395)) ([3fe78dc](https://github.com/ruhex/BGPLite/commit/3fe78dcfc653c254e71ad067f83e3fa318da7127))
* **server:** per-peer MaxPrefix override ([#391](https://github.com/ruhex/BGPLite/issues/391)) ([#393](https://github.com/ruhex/BGPLite/issues/393)) ([3eaf335](https://github.com/ruhex/BGPLite/commit/3eaf3350c89f3c8a1a3e517bd16706a4509fe676))
* **server:** per-peer prefix limit — Cease/MaxPrefixesExceeded on breach ([#304](https://github.com/ruhex/BGPLite/issues/304)) ([7e7f785](https://github.com/ruhex/BGPLite/commit/7e7f7853f09ba1ec49dafaa158b6c8a50d37117e))


### Bug Fixes

* address CodeRabbit findings on the fourth integration ([#377](https://github.com/ruhex/BGPLite/issues/377) review) ([b5ba3b3](https://github.com/ruhex/BGPLite/commit/b5ba3b3fe5432c5c80e45e0e172149577a7545bd))
* address CodeRabbit findings on the integration batch ([#358](https://github.com/ruhex/BGPLite/issues/358) review) ([7510d18](https://github.com/ruhex/BGPLite/commit/7510d18b3b4b2d0f978a06bbda5f4550a1925290))
* address four CodeRabbit findings from the [#503](https://github.com/ruhex/BGPLite/issues/503) integration review ([9820531](https://github.com/ruhex/BGPLite/commit/9820531456c834308a630ecef1daed4ec19072c7))
* address the CodeRabbit findings on the integration review ([#413](https://github.com/ruhex/BGPLite/issues/413)) ([#414](https://github.com/ruhex/BGPLite/issues/414)) ([b5eabd2](https://github.com/ruhex/BGPLite/commit/b5eabd28b0a852455a5eff6c75db8170d086ee3c))
* address the CodeRabbit findings on the integration review ([#450](https://github.com/ruhex/BGPLite/issues/450)) ([#451](https://github.com/ruhex/BGPLite/issues/451)) ([0d27a8e](https://github.com/ruhex/BGPLite/commit/0d27a8e8a430858dcdb5ce6c87a032859351f57d))
* address the CodeRabbit findings on the integration review (3 fixes) ([#388](https://github.com/ruhex/BGPLite/issues/388)) ([dff2ad6](https://github.com/ruhex/BGPLite/commit/dff2ad6d06e099b1d631083dff67a724f19fa76b))
* address the CodeRabbit findings on the integration review, pass 2 ([#398](https://github.com/ruhex/BGPLite/issues/398)) ([#399](https://github.com/ruhex/BGPLite/issues/399)) ([355736e](https://github.com/ruhex/BGPLite/commit/355736e1f29f0fcabf09d51416682169099ecaa0))
* address the CodeRabbit findings on the IPv6 integration review ([#403](https://github.com/ruhex/BGPLite/issues/403)) ([#404](https://github.com/ruhex/BGPLite/issues/404)) ([e062212](https://github.com/ruhex/BGPLite/commit/e062212143236059dfffc1e15a87e59628c52542))
* address the second-pass CodeRabbit findings on [#377](https://github.com/ruhex/BGPLite/issues/377) ([#306](https://github.com/ruhex/BGPLite/issues/306) hardening) ([3cdabce](https://github.com/ruhex/BGPLite/commit/3cdabce51c074bdf933336b6d84af17a394e3286))
* **api,server:** NULL-Asn peer delete terminates its sessions by IP ([#422](https://github.com/ruhex/BGPLite/issues/422)) ([#439](https://github.com/ruhex/BGPLite/issues/439)) ([91c1eb7](https://github.com/ruhex/BGPLite/commit/91c1eb740724ce61affaa3249fbe2205d1416200))
* **api,server:** re-arm the per-IP TCP-MD5 key from surviving sibling rows ([#418](https://github.com/ruhex/BGPLite/issues/418)) ([#435](https://github.com/ruhex/BGPLite/issues/435)) ([a4633af](https://github.com/ruhex/BGPLite/commit/a4633af8772b30fdfcafcd5cb26bd55267c2f78a))
* **api,server:** terminate the deleted peer's live sessions before removing its row ([#323](https://github.com/ruhex/BGPLite/issues/323)) ([7599799](https://github.com/ruhex/BGPLite/commit/7599799b209521828904cffec7b05b83db6f3a00))
* **api:** 266 part 1 — txt export double serialization, CORS PATCH, preflight rate limit, FK 404 ([#266](https://github.com/ruhex/BGPLite/issues/266)) ([a5bf2e2](https://github.com/ruhex/BGPLite/commit/a5bf2e24c4bdf9946652ce71968a24283fdfcbf4))
* **api:** add multihop to MikroTik setup template ([#218](https://github.com/ruhex/BGPLite/issues/218)) ([#219](https://github.com/ruhex/BGPLite/issues/219)) ([e5282e2](https://github.com/ruhex/BGPLite/commit/e5282e292bf2f19fd82433dd7623691b11de4c56))
* **api:** arm the create path's shared-IP TCP-MD5 key through the [#418](https://github.com/ruhex/BGPLite/issues/418) resolver ([#455](https://github.com/ruhex/BGPLite/issues/455)) ([3cfd9a2](https://github.com/ruhex/BGPLite/commit/3cfd9a223c48f0b37045e46c80a68175beca9eb0))
* **api:** atomic PeerStore mutations + save-time SSRF validation + upsert race fix ([#240](https://github.com/ruhex/BGPLite/issues/240)) ([a7e0284](https://github.com/ruhex/BGPLite/commit/a7e02844dfd25435cdbced078a1c9c251efd1b0f))
* **api:** bound cold-cache external-fetch GETs with a wall-clock budget ([#424](https://github.com/ruhex/BGPLite/issues/424)) ([#443](https://github.com/ruhex/BGPLite/issues/443)) ([e600750](https://github.com/ruhex/BGPLite/commit/e600750386215cc80f5adf866ddf2896f707218d))
* **api:** bound each request-body read with a deadline ([#257](https://github.com/ruhex/BGPLite/issues/257)) ([b9fe616](https://github.com/ruhex/BGPLite/commit/b9fe6169d2a1b21db1ab711322537980ef90256a))
* **api:** bound GET /api/as/{asn}/prefixes?count=true with the external-fetch budget ([#454](https://github.com/ruhex/BGPLite/issues/454)) ([1b33974](https://github.com/ruhex/BGPLite/commit/1b339749b0ac4ad54973c4e4337e9dbf61db9553))
* **api:** cancel in-flight handlers on shutdown and bound the drain ([#326](https://github.com/ruhex/BGPLite/issues/326)) ([06e234f](https://github.com/ruhex/BGPLite/commit/06e234f8ea53e5665a21ed94b10784e7b88e7d60))
* **api:** converge legacy Peers columns before the Init stamp ([#264](https://github.com/ruhex/BGPLite/issues/264)) ([24b3d17](https://github.com/ruhex/BGPLite/commit/24b3d17f5c3c607f99c2bdf248ea153f788ebaaa))
* **api:** create/update a peer in one transaction, and dedup its collections ([#310](https://github.com/ruhex/BGPLite/issues/310)) ([3d251d3](https://github.com/ruhex/BGPLite/commit/3d251d3bd1984457f19128d8fece11071dd23d58)), closes [#259](https://github.com/ruhex/BGPLite/issues/259)
* **api:** degrade /api/asn-lists to the partial response when the fetch budget expires mid-load ([#480](https://github.com/ruhex/BGPLite/issues/480)) ([8c6f7f1](https://github.com/ruhex/BGPLite/commit/8c6f7f10c6cb20b9b18ef5b0386c0e69b7529fab))
* **api:** evict idle rate-limiter partitions instead of growing forever ([#423](https://github.com/ruhex/BGPLite/issues/423)) ([#446](https://github.com/ruhex/BGPLite/issues/446)) ([eae0017](https://github.com/ruhex/BGPLite/commit/eae0017e377b84ebeb0464f97f54aabf5d87e49f))
* **api:** hot-reload MaxRequestBodyBytes + sanitize CustomAsns log ([#266](https://github.com/ruhex/BGPLite/issues/266)) ([#382](https://github.com/ruhex/BGPLite/issues/382)) ([bae3007](https://github.com/ruhex/BGPLite/commit/bae3007ea4320f876366ee9c9dbde8bde2cca985))
* **api:** ignore X-Real-IP by default — opt-in via Api.TrustXRealIp ([#256](https://github.com/ruhex/BGPLite/issues/256)) ([#381](https://github.com/ruhex/BGPLite/issues/381)) ([d7b4014](https://github.com/ruhex/BGPLite/commit/d7b40147b29fd0538c359a13dd04c427eea7a40a))
* **api:** keep source URLs with query tokens out of the logs ([#479](https://github.com/ruhex/BGPLite/issues/479)) ([5ae3583](https://github.com/ruhex/BGPLite/commit/5ae3583a528d3ac940a53c1d0f2d02fb3608c259))
* **api:** map SQLite constraint classes by extended code, not a 409 catch-all ([#431](https://github.com/ruhex/BGPLite/issues/431)) ([#441](https://github.com/ruhex/BGPLite/issues/441)) ([894ca4d](https://github.com/ruhex/BGPLite/commit/894ca4df750a6b6c6437c339645038c6b78068aa))
* **api:** reject non-unicast peer IPs at the management API ([#421](https://github.com/ruhex/BGPLite/issues/421)) ([#440](https://github.com/ruhex/BGPLite/issues/440)) ([09afd98](https://github.com/ruhex/BGPLite/commit/09afd98140c540dc8d93a7bcc062a1e028818204))
* **api:** reject unknown subscription list names at the boundary ([#266](https://github.com/ruhex/BGPLite/issues/266) item 4) ([30fc838](https://github.com/ruhex/BGPLite/commit/30fc83833e16f61a468154b083720d50aa62be69))
* **api:** replace EnsureCreated + ad-hoc DDL with EF Migrations ([#249](https://github.com/ruhex/BGPLite/issues/249)) ([5b0c0ba](https://github.com/ruhex/BGPLite/commit/5b0c0ba285c5a97e221af3669b4ec3310810ab89))
* **api:** replace the append-only in-flight handler list with a count + idle task ([#258](https://github.com/ruhex/BGPLite/issues/258)) ([e7f017b](https://github.com/ruhex/BGPLite/commit/e7f017b8e5ef88904d92a24baf63524d9ab837bc))
* **api:** validate and canonicalize peer input before it is persisted ([#311](https://github.com/ruhex/BGPLite/issues/311)) ([a8c0a8d](https://github.com/ruhex/BGPLite/commit/a8c0a8d1ef143bab0823b6bf60db52270837d9ec))
* **api:** validate the AddSource community at the boundary ([#266](https://github.com/ruhex/BGPLite/issues/266) item 3) ([338c46f](https://github.com/ruhex/BGPLite/commit/338c46f6c150d910b73e832a8e7bbc418b37d54c))
* **config,server,providers:** tolerate the documented PrefixSources YAML-null ([#477](https://github.com/ruhex/BGPLite/issues/477)) ([3429972](https://github.com/ruhex/BGPLite/commit/342997246cb2b5bacfd48ab1220b668b2c2b0c69))
* **config:** fail loud on peer 0.0.0.0/missing RemoteAsn + negative tunables ([#390](https://github.com/ruhex/BGPLite/issues/390)) ([#396](https://github.com/ruhex/BGPLite/issues/396)) ([c955911](https://github.com/ruhex/BGPLite/commit/c955911f85349356aec3b9c6bd71422af84d37cf))
* **config:** reject Bgp.HoldTime above the 2-octet OPEN field range ([#265](https://github.com/ruhex/BGPLite/issues/265) item 2) ([#362](https://github.com/ruhex/BGPLite/issues/362)) ([21079a4](https://github.com/ruhex/BGPLite/commit/21079a4bce521bf024b055e149c0de86eee26ea5))
* **config:** ship a bounded MaxPrefixesPerPeer default of 1M ([#481](https://github.com/ruhex/BGPLite/issues/481)) ([d476076](https://github.com/ruhex/BGPLite/commit/d4760766f93589c66eba1243f1977f5a85b4d81e))
* **config:** validate PrefixSources and every config community at startup ([#327](https://github.com/ruhex/BGPLite/issues/327)) ([d7150fa](https://github.com/ruhex/BGPLite/commit/d7150fa8d446a75741d48f09c7a73d2a31d471f8))
* **protocol,api:** unify CIDR parsing — single PrefixCidr with host-bit masking and /0 rejection ([#243](https://github.com/ruhex/BGPLite/issues/243)) ([c717520](https://github.com/ruhex/BGPLite/commit/c71752099ec3e78922e4f7652d5fcb41235c1762)), closes [#236](https://github.com/ruhex/BGPLite/issues/236)
* **protocol,routing,api:** address 2026-08-03 audit findings ([#248](https://github.com/ruhex/BGPLite/issues/248)) ([1e76eb9](https://github.com/ruhex/BGPLite/commit/1e76eb96c2ca1c3818d3cd90cd96a276662a9c86))
* **protocol:** bound the UPDATE withdrawn-routes section — a 23-byte frame no longer kills the session ([#293](https://github.com/ruhex/BGPLite/issues/293)) ([bfa703c](https://github.com/ruhex/BGPLite/commit/bfa703c6dfd62c0407d54f291f562cc1cb88b900)), closes [#284](https://github.com/ruhex/BGPLite/issues/284)
* **protocol:** carry specific RFC 4271 §6.3 subcodes instead of Unspecific ([#245](https://github.com/ruhex/BGPLite/issues/245)) ([91bc62e](https://github.com/ruhex/BGPLite/commit/91bc62ef3512e9ab099ac4127e68f7d56d0a46ec))
* **protocol:** carry the max-supported-version Data field in Unsupported Version NOTIFICATIONs ([#317](https://github.com/ruhex/BGPLite/issues/317)) ([83c21df](https://github.com/ruhex/BGPLite/commit/83c21dfd09401bed3531c03f4e92a9cfcd7c5585))
* **protocol:** CeaseAdministrativeReset is subcode 4 per RFC 4486 §3 ([#506](https://github.com/ruhex/BGPLite/issues/506)) ([f79d5dd](https://github.com/ruhex/BGPLite/commit/f79d5ddab6a53a557abbe7149212546bf98d451b))
* **protocol:** enforce the RFC error policy for MP_REACH/MP_UNREACH ([#467](https://github.com/ruhex/BGPLite/issues/467)) ([f69b5a9](https://github.com/ruhex/BGPLite/commit/f69b5a9792de703956518f5fc82f392b60481061))
* **protocol:** handle malformed UPDATE without tearing down session ([#239](https://github.com/ruhex/BGPLite/issues/239)) ([8290688](https://github.com/ruhex/BGPLite/commit/8290688f3341d983e08b4768615ba77371980281))
* **protocol:** implement MP IPv4/Unicast receive and advertise the capability ([#466](https://github.com/ruhex/BGPLite/issues/466)) ([6cd942c](https://github.com/ruhex/BGPLite/commit/6cd942ceddd0079882988a27bc2eaf6484f319b1))
* **protocol:** keep the first occurrence of a duplicated path attribute ([#296](https://github.com/ruhex/BGPLite/issues/296)) ([5f2def0](https://github.com/ruhex/BGPLite/commit/5f2def0ca44b36cdf179e05de1e46d17a0f4c2b3)), closes [#287](https://github.com/ruhex/BGPLite/issues/287)
* **protocol:** make the Extended Length flag match the length field the writer emits ([#299](https://github.com/ruhex/BGPLite/issues/299)) ([b8283b6](https://github.com/ruhex/BGPLite/commit/b8283b6948d22c84aa6d1cc16e2dac19fad385eb)), closes [#291](https://github.com/ruhex/BGPLite/issues/291)
* **protocol:** negotiate hold time as min(local, peer) per RFC 4271 §6.2.2 ([#241](https://github.com/ruhex/BGPLite/issues/241)) ([02d7202](https://github.com/ruhex/BGPLite/commit/02d720270974335c0297199fc4667c9137145ba4)), closes [#224](https://github.com/ruhex/BGPLite/issues/224)
* **protocol:** reject a community VALUE above 65535 instead of masking it ([#328](https://github.com/ruhex/BGPLite/issues/328)) ([7aeb114](https://github.com/ruhex/BGPLite/commit/7aeb11454180e04a38d9f6a62b684a56edadc1b4))
* **protocol:** reject a zero-length AS_PATH at the eBGP policy layer ([#486](https://github.com/ruhex/BGPLite/issues/486)) ([867c8e6](https://github.com/ruhex/BGPLite/commit/867c8e6edfb35d1f4ce64c781a512c000d2277de))
* **protocol:** reject an unrecognized well-known path attribute with subcode 2 ([#322](https://github.com/ruhex/BGPLite/issues/322)) ([3663d2f](https://github.com/ruhex/BGPLite/commit/3663d2fe9d03b222b4520e7059b083bb8de8666c))
* **protocol:** reject semantically invalid NEXT_HOP with subcode 8 ([#360](https://github.com/ruhex/BGPLite/issues/360)) ([261aa39](https://github.com/ruhex/BGPLite/commit/261aa3953f7b5713a17a4c76ee36a7e0697defa9))
* **protocol:** reject truncated OPEN optional-params/capabilities ([#244](https://github.com/ruhex/BGPLite/issues/244)) ([184f194](https://github.com/ruhex/BGPLite/commit/184f1940c2c326c23c0965d04a434bd77ab2bfc6))
* **protocol:** reject unsupported OPEN optional parameters with 2/4 ([#329](https://github.com/ruhex/BGPLite/issues/329)) ([96221c0](https://github.com/ruhex/BGPLite/commit/96221c0929562bb0b421551aa5b96ed1166df9f9))
* **protocol:** reserved flag 0x08, writer attribute ordering, COMMUNITY % 4 ([#273](https://github.com/ruhex/BGPLite/issues/273)) ([fd0fb77](https://github.com/ruhex/BGPLite/commit/fd0fb77db015ae5dc9f275750c9b9e75baee666a))
* **protocol:** RFC 4271 6.1 header subcodes + KEEPALIVE length, and reject AS 0 per RFC 7607 ([#301](https://github.com/ruhex/BGPLite/issues/301)) ([9708cdc](https://github.com/ruhex/BGPLite/commit/9708cdce5e4622786b7bcdec8174f07132385196)), closes [#300](https://github.com/ruhex/BGPLite/issues/300)
* **protocol:** scope MP error recovery to the AFI/SAFI tuple ([#467](https://github.com/ruhex/BGPLite/issues/467) review) ([ec5069c](https://github.com/ruhex/BGPLite/commit/ec5069ccd8912f61060fa6c73b19b81131d89bf9))
* **protocol:** scope the [#3](https://github.com/ruhex/BGPLite/issues/3)(j) MP fallback to the failing AFI/SAFI tuple ([#472](https://github.com/ruhex/BGPLite/issues/472) review) ([6a15095](https://github.com/ruhex/BGPLite/commit/6a15095347103315d8f449917a20da236b054317))
* **protocol:** stop advertising the Graceful Restart capability ([#318](https://github.com/ruhex/BGPLite/issues/318)) ([#384](https://github.com/ruhex/BGPLite/issues/384)) ([5ed2925](https://github.com/ruhex/BGPLite/commit/5ed2925fe3485406a4d1f375bac703b46c3d11e7))
* **protocol:** stop advertising the MP IPv4/Unicast capability ([#466](https://github.com/ruhex/BGPLite/issues/466)) ([f56252e](https://github.com/ruhex/BGPLite/commit/f56252e7cee238cc1ec25163d26296201c5e1747))
* **protocol:** validate attribute flags against the type code and enforce fixed lengths ([#297](https://github.com/ruhex/BGPLite/issues/297)) ([28bcb30](https://github.com/ruhex/BGPLite/commit/28bcb306e41f03cb0830d25e7dad97c53e35acfc)), closes [#290](https://github.com/ruhex/BGPLite/issues/290)
* **protocol:** validate received ORIGIN is 0/1/2 per RFC 4271 §5.1.2 ([#247](https://github.com/ruhex/BGPLite/issues/247)) ([4f7e2ff](https://github.com/ruhex/BGPLite/commit/4f7e2ff7e3efb38425f6dc9a43b3900363edf203))
* **providers,api:** GetRuPrefixesAsync cache race + collapse BuildPeerDetail N+1 DbContexts ([#242](https://github.com/ruhex/BGPLite/issues/242)) ([b656a53](https://github.com/ruhex/BGPLite/commit/b656a5333111eb721d0afb5b6533c6ab44ada9dd))
* **providers,server:** arm a fetch budget for user URL sources; treat its OCE as a load failure ([#320](https://github.com/ruhex/BGPLite/issues/320)) ([77f8144](https://github.com/ruhex/BGPLite/commit/77f8144d4ef8d38e18c48996dee38ba80b461826))
* providers/config audit minors — reload re-arm, redirect rejection, NAT64, RIPEstat bounds, async file reads, empty-YAML error, refresh teardown ([#321](https://github.com/ruhex/BGPLite/issues/321)) ([add9d93](https://github.com/ruhex/BGPLite/commit/add9d93305f247e6576aaf4b7e311db537408d0c))
* **providers:** a cancelled waiter must not split the UserSourceCache gate ([#468](https://github.com/ruhex/BGPLite/issues/468)) ([90d4a9a](https://github.com/ruhex/BGPLite/commit/90d4a9ae2075c7cd2277da18064551435a3b224a))
* **providers:** apply the caller-vs-foreign OCE contract at three stragglers ([#485](https://github.com/ruhex/BGPLite/issues/485)) ([ab56968](https://github.com/ruhex/BGPLite/commit/ab569684f9e4a8ddfa31dab701f11fe10adeb56f))
* **providers:** arm the fetch budget for config sources; stop the pipeline from clipping configured timeouts ([#324](https://github.com/ruhex/BGPLite/issues/324)) ([b428934](https://github.com/ruhex/BGPLite/commit/b428934ca7ef1cd696175d65e9bab24bd268051c))
* **providers:** block the RFC 8215 NAT64 local-use prefix in the SSRF filter ([#419](https://github.com/ruhex/BGPLite/issues/419)) ([#436](https://github.com/ruhex/BGPLite/issues/436)) ([f617697](https://github.com/ruhex/BGPLite/commit/f617697bb5144c0a9c0b5d9fc52ec4bf60755f48))
* **providers:** cap UserSourceCache entries — port the [#165](https://github.com/ruhex/BGPLite/issues/165) eviction sweep ([#261](https://github.com/ruhex/BGPLite/issues/261)) ([e520b8d](https://github.com/ruhex/BGPLite/commit/e520b8de0ce3bb27ad436311e35f9e4bac72f547))
* **providers:** fetch peer-supplied user sources on a breaker-free pipeline ([#425](https://github.com/ruhex/BGPLite/issues/425)) ([#444](https://github.com/ruhex/BGPLite/issues/444)) ([e94aa9a](https://github.com/ruhex/BGPLite/commit/e94aa9a9698a656e864d860ec2c6c58d64db1e17))
* **providers:** fire the RU convergence push off the _ruGate holder's stack ([#416](https://github.com/ruhex/BGPLite/issues/416)) ([#433](https://github.com/ruhex/BGPLite/issues/433)) ([79f0299](https://github.com/ruhex/BGPLite/commit/79f029951c8993c47cce96884f410422931ba0d8))
* **providers:** invalidate the RU projection when the default source changes on the auto-refresh path ([#452](https://github.com/ruhex/BGPLite/issues/452)) ([7f81bf3](https://github.com/ruhex/BGPLite/commit/7f81bf31d4251f281f8d62660d48c2a849273b4f))
* **providers:** make UserSourceCache gate bookkeeping one atomic section ([#478](https://github.com/ruhex/BGPLite/issues/478)) ([4e91a12](https://github.com/ruhex/BGPLite/commit/4e91a12a355a39160b6ea912317116212d547b58))
* **providers:** never cache a failed default-source load as an empty RU set ([#417](https://github.com/ruhex/BGPLite/issues/417)) ([#434](https://github.com/ruhex/BGPLite/issues/434)) ([348db70](https://github.com/ruhex/BGPLite/commit/348db70f6397dbc6eaee2b65bb91af1aa7ff8161))
* **providers:** no eviction of an in-flight ASN's gate; drop post-create roundtrip ([#267](https://github.com/ruhex/BGPLite/issues/267)) ([#383](https://github.com/ruhex/BGPLite/issues/383)) ([9c5451e](https://github.com/ruhex/BGPLite/commit/9c5451ea28deb1ee1a4294c1caed5946f7dc07af))
* **providers:** parse RIPEstat prefixes through the canonical PrefixCidr parser ([#319](https://github.com/ruhex/BGPLite/issues/319)) ([7ffb332](https://github.com/ruhex/BGPLite/commit/7ffb3323e48012aa1944e805d97af98a4a559c19))
* **providers:** sweep expired cache entries and bound UserSourceCache memory ([#426](https://github.com/ruhex/BGPLite/issues/426)) ([#445](https://github.com/ruhex/BGPLite/issues/445)) ([a003e8e](https://github.com/ruhex/BGPLite/commit/a003e8ed2eb1acaa4fa95db2e4fe351c95095236))
* **routing,server:** a withdrawal removes only the route this peer currently owns ([#303](https://github.com/ruhex/BGPLite/issues/303)) ([58c98bf](https://github.com/ruhex/BGPLite/commit/58c98bffe2cf2efbd8850ba864677983afa924bc)), closes [#289](https://github.com/ruhex/BGPLite/issues/289)
* **routing:** community-less routes are denied under an active per-peer allowlist ([#389](https://github.com/ruhex/BGPLite/issues/389)) ([#394](https://github.com/ruhex/BGPLite/issues/394)) ([f55beff](https://github.com/ruhex/BGPLite/commit/f55beff85db657bcea34a28ac9c8b4f812a2326e))
* **routing:** delete a peer's routes when its session ends ([#314](https://github.com/ruhex/BGPLite/issues/314)) ([f215db4](https://github.com/ruhex/BGPLite/commit/f215db4ee0fa15058483cc77f5e3017c1ff533b1)), closes [#313](https://github.com/ruhex/BGPLite/issues/313)
* **routing:** fail closed on total source failure, warn on unknown subscriptions ([#488](https://github.com/ruhex/BGPLite/issues/488)) ([5b2d857](https://github.com/ruhex/BGPLite/commit/5b2d85738c6825f714ef93025bc660e74e673636))
* **routing:** the shared-table fallback must not advertise peer-injected routes ([#308](https://github.com/ruhex/BGPLite/issues/308)) ([78c19c5](https://github.com/ruhex/BGPLite/commit/78c19c5fdf2d10b3d173cf503188193f76b9e78c)), closes [#307](https://github.com/ruhex/BGPLite/issues/307)
* **server:** a replaced session's teardown no longer overwrites the live peer status ([#265](https://github.com/ruhex/BGPLite/issues/265) item 1) ([240fe56](https://github.com/ruhex/BGPLite/commit/240fe565d2460fab989add17d7aa1d4bbf9c4757))
* **server:** an OPEN received in Established is an FSM error ([#427](https://github.com/ruhex/BGPLite/issues/427)) ([#438](https://github.com/ruhex/BGPLite/issues/438)) ([fc5f4d7](https://github.com/ruhex/BGPLite/commit/fc5f4d7138b7efae27e2640d07a3bbd4352e00af))
* **server:** an unexpected message in Established is an FSM error, not silence ([#265](https://github.com/ruhex/BGPLite/issues/265) item 3) ([#361](https://github.com/ruhex/BGPLite/issues/361)) ([c8e24c5](https://github.com/ruhex/BGPLite/commit/c8e24c5c496a0f65b1fe4e6b35a95102395ee10d))
* **server:** answer an OPEN received in OpenConfirm with FSM error (5/0) ([#453](https://github.com/ruhex/BGPLite/issues/453)) ([daaf39c](https://github.com/ruhex/BGPLite/commit/daaf39cee417af71ef4e2f09ba6d05603986593c))
* **server:** apply the MP_UNREACH_NLRI half of an UPDATE during treat-as-withdraw ([#484](https://github.com/ruhex/BGPLite/issues/484)) ([c785961](https://github.com/ruhex/BGPLite/commit/c78596193168f289832394d984000d1851eee3eb))
* **server:** bound the accept-loop retry rate on persistent accept failures ([#428](https://github.com/ruhex/BGPLite/issues/428)) ([#442](https://github.com/ruhex/BGPLite/issues/442)) ([763b4bd](https://github.com/ruhex/BGPLite/commit/763b4bd580dd7de2b3b5674ca25edf843fb3b236))
* **server:** carry IsIpv4 through MergeDuplicatePrefixes ([#476](https://github.com/ruhex/BGPLite/issues/476)) ([1679714](https://github.com/ruhex/BGPLite/commit/16797140ad60f2f3ff99eb5f8e11a921cb0b2446))
* **server:** drop an unsplittable oversized UPDATE group instead of tearing the session down ([#457](https://github.com/ruhex/BGPLite/issues/457)) ([7a14a57](https://github.com/ruhex/BGPLite/commit/7a14a576ec44bec32a9cdf6114c7776d7653abdc))
* **server:** enforce a real per-send timeout — async writes were unbounded ([#279](https://github.com/ruhex/BGPLite/issues/279)) ([160adff](https://github.com/ruhex/BGPLite/commit/160adff5adb96244ddde24f3fe28c1a735c4980d))
* **server:** exclude looping routes — local ASN in AS_PATH is never installed ([#292](https://github.com/ruhex/BGPLite/issues/292) item 6) ([6dcc552](https://github.com/ruhex/BGPLite/commit/6dcc5527e6cf0b985b9a63d5d1f1e586f53d1e9d))
* **server:** guard the four teardown dispose-races in BgpSession ([#482](https://github.com/ruhex/BGPLite/issues/482)) ([da79487](https://github.com/ruhex/BGPLite/commit/da79487861785c750048844ef201f0f63abb6d8a))
* **server:** guard UpdateSessionStatus in the RunAsync finally ([#325](https://github.com/ruhex/BGPLite/issues/325)) ([6202ae8](https://github.com/ruhex/BGPLite/commit/6202ae83eaf589a5786b2f59ab0dd3c5aadb163e))
* **server:** handle ROUTE_REFRESH off the read loop — slow refresh no longer kills a live session ([#283](https://github.com/ruhex/BGPLite/issues/283)) ([1224144](https://github.com/ruhex/BGPLite/commit/1224144e4472b36f28693b491b22f6c8d7266962))
* **server:** handle the pre-OPEN phase per the FSM — never reply to a NOTIFICATION, 5/0 for other non-OPEN first messages ([#483](https://github.com/ruhex/BGPLite/issues/483)) ([2db1072](https://github.com/ruhex/BGPLite/commit/2db1072d8faf542300e6b443939823ba268bcd1d))
* **server:** honor ROUTE_REFRESH for AFI=2 on MP-IPv6 sessions ([#420](https://github.com/ruhex/BGPLite/issues/420)) ([#437](https://github.com/ruhex/BGPLite/issues/437)) ([9972482](https://github.com/ruhex/BGPLite/commit/9972482dcb705408b71d7d152d8ba97b07510f93))
* **server:** keep _advertisedCount at wire truth when a group is dropped ([#457](https://github.com/ruhex/BGPLite/issues/457) review) ([87f9859](https://github.com/ruhex/BGPLite/commit/87f98596389150f8d4855d8482f1ebde7869feb3))
* **server:** log explicit cause when peer closes TCP connection ([#217](https://github.com/ruhex/BGPLite/issues/217)) ([003a00f](https://github.com/ruhex/BGPLite/commit/003a00f50e86761e44811015330409256dfb3d8b))
* **server:** normalize RefreshRoutesAsync token and coalesce stacked refreshes ([#280](https://github.com/ruhex/BGPLite/issues/280)) ([fc95a44](https://github.com/ruhex/BGPLite/commit/fc95a4427a2550eecc142ce56dc5d04ea377275c))
* **server:** process MP_REACH/MP_UNREACH-only UPDATEs ([#407](https://github.com/ruhex/BGPLite/issues/407)) ([#408](https://github.com/ruhex/BGPLite/issues/408)) ([c983292](https://github.com/ruhex/BGPLite/commit/c983292fabab2a837d22d4b45849ac0ba07e903b))
* **server:** record the send mirror only after a batch reaches the wire ([#430](https://github.com/ruhex/BGPLite/issues/430)) ([#448](https://github.com/ruhex/BGPLite/issues/448)) ([66d1015](https://github.com/ruhex/BGPLite/commit/66d1015ad36cf1e645bb650b32febbdf6ee573f5))
* **server:** register BgpServer as a DI singleton, drop the captured-local wiring ([#250](https://github.com/ruhex/BGPLite/issues/250)) ([a4576be](https://github.com/ruhex/BGPLite/commit/a4576be05794c1d42a7db775f4c4e35fcd5df7f1))
* **server:** repair the status row when a replacement lands during the inactive write ([#366](https://github.com/ruhex/BGPLite/issues/366) review) ([d69d85d](https://github.com/ruhex/BGPLite/commit/d69d85d4d7b3a8e0a5ebc2c8475c0a0e8afb1bd2))
* **server:** rethrow BgpNotificationException from AwaitLoopTaskAsync — the MaxPrefixes reset keeps its Cease subcode ([#505](https://github.com/ruhex/BGPLite/issues/505)) ([251c401](https://github.com/ruhex/BGPLite/commit/251c401cbabc1404b90f96cf1fe94f145a360761))
* **server:** run the hold timer in OpenConfirm — a silent peer no longer pins a session ([#295](https://github.com/ruhex/BGPLite/issues/295)) ([c060749](https://github.com/ruhex/BGPLite/commit/c0607494ed14700f9d6dfd223ce5d1c20d638f92)), closes [#286](https://github.com/ruhex/BGPLite/issues/286)
* **server:** tear down on an aborted send instead of writing behind a truncated frame ([#294](https://github.com/ruhex/BGPLite/issues/294)) ([6ab1c6b](https://github.com/ruhex/BGPLite/commit/6ab1c6b0030dffedfda2049309d3c2e8cbcf2386)), closes [#285](https://github.com/ruhex/BGPLite/issues/285)
* **server:** treat a live-token user-source OCE as a fetch failure ([#342](https://github.com/ruhex/BGPLite/issues/342)) ([dcf59d4](https://github.com/ruhex/BGPLite/commit/dcf59d4c148f66dbf75212dff79cfe560df4a054))
* **server:** treat-as-withdraw now withdraws the UPDATE's NLRI ([#298](https://github.com/ruhex/BGPLite/issues/298)) ([e9aafe1](https://github.com/ruhex/BGPLite/commit/e9aafe1351e1bdd8c4f9f4cc16369612ee23a3c4)), closes [#288](https://github.com/ruhex/BGPLite/issues/288)
* **server:** unwind sends parked on _sendLock at Dispose instead of hanging RunAsync ([#341](https://github.com/ruhex/BGPLite/issues/341)) ([78aadf9](https://github.com/ruhex/BGPLite/commit/78aadf9ffd52602a7e1e5807034b4aa4af110f5f))
* **startup:** seed routes in the background — listeners start immediately ([#278](https://github.com/ruhex/BGPLite/issues/278)) ([ab4841a](https://github.com/ruhex/BGPLite/commit/ab4841acb25433d264c22a79c673123f05613969))


### Performance Improvements

* **api:** split the peer reads — a Cartesian product was costing up to 814 ms per call ([#309](https://github.com/ruhex/BGPLite/issues/309)) ([9a049bd](https://github.com/ruhex/BGPLite/commit/9a049bd5dde140a5c22644f5deeb8c8cde5572c0)), closes [#260](https://github.com/ruhex/BGPLite/issues/260)
* **routing:** maintain RouteTable.Count instead of per-UPDATE all-lock reads ([#343](https://github.com/ruhex/BGPLite/issues/343)) ([2e53641](https://github.com/ruhex/BGPLite/commit/2e53641366a7a7e29be0abfddd3960292d3dd08d))
* **routing:** memoize community normalization per Aggregate call ([#315](https://github.com/ruhex/BGPLite/issues/315)) ([0c4ae32](https://github.com/ruhex/BGPLite/commit/0c4ae32da498d8dfa30dd071a0df779818cbee0b)), closes [#305](https://github.com/ruhex/BGPLite/issues/305)
* **server,providers:** index custom-prefix suppression and memoize the contract projection ([#429](https://github.com/ruhex/BGPLite/issues/429)) ([#447](https://github.com/ruhex/BGPLite/issues/447)) ([384d1e3](https://github.com/ruhex/BGPLite/commit/384d1e3522981eb6e53739a80783981c3f271f3b))

## [1.5.0](https://github.com/ruhex/BGPLite/compare/v1.4.5...v1.5.0) (2026-07-08)


### Features

* **api:** show actual advertised prefix count (post-aggregation) in API ([#212](https://github.com/ruhex/BGPLite/issues/212)) ([970af4d](https://github.com/ruhex/BGPLite/commit/970af4d50a80c58688b70709561a8057e330f7cf))


### Bug Fixes

* **server:** merge duplicate NLRI — union communities for shared prefixes ([#209](https://github.com/ruhex/BGPLite/issues/209)) ([67bf44b](https://github.com/ruhex/BGPLite/commit/67bf44b9e192cb66bf4bedd8432ad9e45f197e6d))

## [1.4.5](https://github.com/ruhex/BGPLite/compare/v1.4.4...v1.4.5) (2026-07-08)


### Bug Fixes

* **server:** expose RemoteAsn on BgpSession for ASN-scoped refresh ([#206](https://github.com/ruhex/BGPLite/issues/206)) ([35d762f](https://github.com/ruhex/BGPLite/commit/35d762f2117ac801f81ed943c0a67b55eae81881))

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
