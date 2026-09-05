# Changelog

## [1.7.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.6.0...v1.7.0) (2026-09-05)


### Features

* **LocalLightsGI:** Simplify logic and always solve max samples ([#114](https://github.com/fresolina/Unity-Voxel-Lighting/issues/114)) ([56ce6d6](https://github.com/fresolina/Unity-Voxel-Lighting/commit/56ce6d6abd0b8e32d7c4ce673d831d786ac28b20))


### Bug Fixes

* **VR:** Add instanced support to VoxelLit shader ([#120](https://github.com/fresolina/Unity-Voxel-Lighting/issues/120)) ([9ca7db2](https://github.com/fresolina/Unity-Voxel-Lighting/commit/9ca7db2673cdd75ce95528b18c899656060c3bc2))

## [1.6.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.5.0...v1.6.0) (2026-08-28)


### Features

* Add GPU profiling for GI solve ([#86](https://github.com/fresolina/Unity-Voxel-Lighting/issues/86)) ([18fa9f9](https://github.com/fresolina/Unity-Voxel-Lighting/commit/18fa9f9bf05dfb3d5c1da856beb7686df03c59e2))
* **BufferGI:** Micro-optimize fragment shader GI hot-path ([#80](https://github.com/fresolina/Unity-Voxel-Lighting/issues/80)) ([a932153](https://github.com/fresolina/Unity-Voxel-Lighting/commit/a93215312f277f8cdcab52e77094304391692204))
* **BufferGi:** Single tap filter + direct-lighting mute for GI A/B ([#105](https://github.com/fresolina/Unity-Voxel-Lighting/issues/105)) ([0b7a24e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/0b7a24e016b6d911d4c29efdefa6f68a1633ed3d))
* **Cleanup:** Merge voxelgi into buffergi ([#84](https://github.com/fresolina/Unity-Voxel-Lighting/issues/84)) ([d551730](https://github.com/fresolina/Unity-Voxel-Lighting/commit/d5517308287e4ce09d68488a45ce593e9ffa05b4))
* Cubed directional irradiance buffers ([7ad30f2](https://github.com/fresolina/Unity-Voxel-Lighting/commit/7ad30f278d299a6ea4e1d92b027b26390d001bc2))
* Cubed directional irradiance buffers ([dd1b16c](https://github.com/fresolina/Unity-Voxel-Lighting/commit/dd1b16c64b659f2dac9b2f3b12f4c4e065b41026))
* Directional (cubed) irradiance buffers ([#100](https://github.com/fresolina/Unity-Voxel-Lighting/issues/100)) ([a7a923e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/a7a923e0214f9ca5035cf7d82088aa70ca8b4d68))
* Improve OcclusionField accuracy and blending ([#92](https://github.com/fresolina/Unity-Voxel-Lighting/issues/92)) ([d1ba293](https://github.com/fresolina/Unity-Voxel-Lighting/commit/d1ba293fa8fd65b95871e1ac0237e8b66ca5091e))
* optimize tonemap and auto exposure ([#89](https://github.com/fresolina/Unity-Voxel-Lighting/issues/89)) ([cad149a](https://github.com/fresolina/Unity-Voxel-Lighting/commit/cad149a1dae8ba50242a12d7d05a6e694caadb1e))
* refactor shaders ([#108](https://github.com/fresolina/Unity-Voxel-Lighting/issues/108)) ([d5c3a45](https://github.com/fresolina/Unity-Voxel-Lighting/commit/d5c3a4543a49fff4001fbc2f8f9964bca1b35132))
* separate wall thickening ([#104](https://github.com/fresolina/Unity-Voxel-Lighting/issues/104)) ([c9b3069](https://github.com/fresolina/Unity-Voxel-Lighting/commit/c9b30697f3b084105e98ab1d46e01b196042c3e8))
* Setup VR demo project ([#82](https://github.com/fresolina/Unity-Voxel-Lighting/issues/82)) ([0b87f12](https://github.com/fresolina/Unity-Voxel-Lighting/commit/0b87f12189c3c04d4ca40c9f814eb166e6a553fc))
* Support loading dynamic local lights from level scene ([#96](https://github.com/fresolina/Unity-Voxel-Lighting/issues/96)) ([5242fa2](https://github.com/fresolina/Unity-Voxel-Lighting/commit/5242fa2257bfa569a911798dcdec066aa1e17c15))
* Support runtime togglable baked voxel lights ([#95](https://github.com/fresolina/Unity-Voxel-Lighting/issues/95)) ([91c07c9](https://github.com/fresolina/Unity-Voxel-Lighting/commit/91c07c90e359bdfa776d196f4ef3f27cadbee434))
* Support supersampling baked shadow field ([#94](https://github.com/fresolina/Unity-Voxel-Lighting/issues/94)) ([aa4cb31](https://github.com/fresolina/Unity-Voxel-Lighting/commit/aa4cb31a28ed8c4c74087ba9f99dcf686670bb19))
* **VoxelLit:** Support alpha cut materials (foliage) ([#97](https://github.com/fresolina/Unity-Voxel-Lighting/issues/97)) ([f6d7f69](https://github.com/fresolina/Unity-Voxel-Lighting/commit/f6d7f6986f7a08eca85f8c40868780e82601af19))
* **VR:** Add controls for moving sun, toggle flashlight candle ([#88](https://github.com/fresolina/Unity-Voxel-Lighting/issues/88)) ([cdc2fc9](https://github.com/fresolina/Unity-Voxel-Lighting/commit/cdc2fc940a103a3fed72be52d8a741b6ca971326))


### Bug Fixes

* **BufferGi:** correct GI read geometry and baked shadow filtering ([#101](https://github.com/fresolina/Unity-Voxel-Lighting/issues/101)) ([6f02d5f](https://github.com/fresolina/Unity-Voxel-Lighting/commit/6f02d5f64df38551c35e7c66312219a45deb6e27))
* **BufferGi:** Improve voxel normals baking, and optimize dark corner fix using this ([#106](https://github.com/fresolina/Unity-Voxel-Lighting/issues/106)) ([41ae71b](https://github.com/fresolina/Unity-Voxel-Lighting/commit/41ae71bc49307e6a2b6d7af14e7a333456129e70))
* deterministic gi convergence independent of samples per frame ([#102](https://github.com/fresolina/Unity-Voxel-Lighting/issues/102)) ([0bd961e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/0bd961e5ea16a745612255efef6e4d664883b79b))
* Fix shadow faulty edge in shadow field and cleanup fields ([#93](https://github.com/fresolina/Unity-Voxel-Lighting/issues/93)) ([40b3ad3](https://github.com/fresolina/Unity-Voxel-Lighting/commit/40b3ad397de73a8dc5310195c7ca4cb2c6f11321))
* gate self-shadow behind geometric normal ([#99](https://github.com/fresolina/Unity-Voxel-Lighting/issues/99)) ([ef74f04](https://github.com/fresolina/Unity-Voxel-Lighting/commit/ef74f048626e9e01350768a72c45765e46105a93))
* Move FlameFlicker script from bundle to core ([#111](https://github.com/fresolina/Unity-Voxel-Lighting/issues/111)) ([52be281](https://github.com/fresolina/Unity-Voxel-Lighting/commit/52be2818c2611063b6c490f526e7764330140fb5))

## [1.5.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.4.0...v1.5.0) (2026-07-11)


### Features

* Add AgX, ACES tonemapping ([#79](https://github.com/fresolina/Unity-Voxel-Lighting/issues/79)) ([215252e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/215252eb8d8e9927ea3fa48a3d2716bffd7bb859))
* **BufferGi:** Bake surface normals into its own buffer ([dc01a03](https://github.com/fresolina/Unity-Voxel-Lighting/commit/dc01a03bea4cf21624af7fecc0d62082dcaf4cce))
* **BufferGi:** Bake voxelized data to disk ([#66](https://github.com/fresolina/Unity-Voxel-Lighting/issues/66)) ([344c314](https://github.com/fresolina/Unity-Voxel-Lighting/commit/344c314524466944f9f231590bf718abc5d66ee3))
* **BufferGi:** Cheap baked voxel shadows ([8762676](https://github.com/fresolina/Unity-Voxel-Lighting/commit/8762676e963429366b6cc5c788ec0ef49f799c94))
* **BufferGi:** Optimize, add air distance to surface buffer ([0e4e36e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/0e4e36e69366a13cdff757fc774b3c6520fa2265))
* **BufferGi:** optimized occupancy buffer, new surface buffer (normals, AO, flags) ([0bf5915](https://github.com/fresolina/Unity-Voxel-Lighting/commit/0bf5915b1ae48bd9adee084a8a730d2302b91b9a))
* **BufferGI:** Show debug UI of voxel buffers ([#69](https://github.com/fresolina/Unity-Voxel-Lighting/issues/69)) ([a2e9b18](https://github.com/fresolina/Unity-Voxel-Lighting/commit/a2e9b18cfb63c143b1e5de40b4c2bf4b1c3220cf))
* load remote scenes, Sponza added ([#68](https://github.com/fresolina/Unity-Voxel-Lighting/issues/68)) ([0da948f](https://github.com/fresolina/Unity-Voxel-Lighting/commit/0da948f7321bec333233a5dbd61159ecfbf1f6eb))
* Major refactor renamed classes ([229ca41](https://github.com/fresolina/Unity-Voxel-Lighting/commit/229ca41d2195d9be81f8658fa569584a0f390249))
* new gi buffer only cache-performant ([46875be](https://github.com/fresolina/Unity-Voxel-Lighting/commit/46875be7a0ba9d9e317d12efdcfb67044789ab38))
* refactor lots, move files to subdirs, extract autoexposure, fix gi radiance self-occlusion ([a8698f1](https://github.com/fresolina/Unity-Voxel-Lighting/commit/a8698f1d38a3706915433223d709dd4bc22450ad))
* Show more lighting controls in UI ([#76](https://github.com/fresolina/Unity-Voxel-Lighting/issues/76)) ([5b400d2](https://github.com/fresolina/Unity-Voxel-Lighting/commit/5b400d2d5d889174aae4b48b40cc725da89e2749))
* Voxelize albedo color and transparency ([#65](https://github.com/fresolina/Unity-Voxel-Lighting/issues/65)) ([f2668d1](https://github.com/fresolina/Unity-Voxel-Lighting/commit/f2668d1aaa8570d13e897e898f7feae022729d40))


### Bug Fixes

* Reduce GI noise with fadein and fix emissive material fireflies ([581ff7b](https://github.com/fresolina/Unity-Voxel-Lighting/commit/581ff7bc890f9f3c4c93b9cd21be9f22ee35898f))
* UI textfield lost focus immediately in WebGL ([#75](https://github.com/fresolina/Unity-Voxel-Lighting/issues/75)) ([f28dff6](https://github.com/fresolina/Unity-Voxel-Lighting/commit/f28dff6f5933c1f439e32acd3c361d1864ef9cd7))

## [1.4.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.3.0...v1.4.0) (2026-06-23)


### Features

* autocalculate raymarch values and let out of steps mean hit (fixes some light leak in corners) ([89eb892](https://github.com/fresolina/Unity-Voxel-Lighting/commit/89eb89241b4ab207c4e272f1bed54700dc84f71e))
* Optimize SDF baker and add alternative Jumpflooding algorithm ([93a29ca](https://github.com/fresolina/Unity-Voxel-Lighting/commit/93a29cab3ccf91b956c094ab98ad32aa7e9353a5))
* Support baking only sun direction, and 8 dir ([1495f41](https://github.com/fresolina/Unity-Voxel-Lighting/commit/1495f416efd7dddbfba8068c69f35b3d01b5663d))
* Support multiple volumes ([5b25003](https://github.com/fresolina/Unity-Voxel-Lighting/commit/5b25003b6a7f0eb09d5f7904094d1d6b7914d7b6))
* **UI:** select shadowmode and toggle UI ([ce919e3](https://github.com/fresolina/Unity-Voxel-Lighting/commit/ce919e38c19f69256358e10562d562856e4d0d09))


### Bug Fixes

* sdf shadow corner never got lit ([d7315bc](https://github.com/fresolina/Unity-Voxel-Lighting/commit/d7315bc4da6e1ccdc873451803b1c852a74f2000))
* sdf shadow corner never got lit ([bb4602c](https://github.com/fresolina/Unity-Voxel-Lighting/commit/bb4602c561f178aa2cf04fb4e6f6000f58c01896))

## [1.3.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.2.0...v1.3.0) (2026-05-23)


### Features

* Add debug frame times to UI ([#44](https://github.com/fresolina/Unity-Voxel-Lighting/issues/44)) ([6bd8d6c](https://github.com/fresolina/Unity-Voxel-Lighting/commit/6bd8d6ce7daa2baf2163256daf42fc33176742c7))
* additional lights ([5ac1c74](https://github.com/fresolina/Unity-Voxel-Lighting/commit/5ac1c7472a1947f6cb1ce2890df72657a4219b10))
* improve path tracing GI ([#40](https://github.com/fresolina/Unity-Voxel-Lighting/issues/40)) ([fd2294e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/fd2294e86fff7ead537450921538f17c6ea0917f))
* occlusion field 2 ([880041f](https://github.com/fresolina/Unity-Voxel-Lighting/commit/880041ff404f254bf616a7e658ad9825c0a57b7c))
* Runtime system control UI ([44336bb](https://github.com/fresolina/Unity-Voxel-Lighting/commit/44336bbed1dde264a3281731b316855f0ea5a59e))


### Bug Fixes

* frame time not accurate, says 16.7 when locked to 60 ([fd03616](https://github.com/fresolina/Unity-Voxel-Lighting/commit/fd0361603fbdc7d04fabf44d52b5470a2825bf22))
* frame timings were too high on locked fps ([3919690](https://github.com/fresolina/Unity-Voxel-Lighting/commit/3919690bf434a8fe2fc139b4186a5514a85a460a))
* improve point and spot light handling in voxel lighting system ([#38](https://github.com/fresolina/Unity-Voxel-Lighting/issues/38)) ([4d09011](https://github.com/fresolina/Unity-Voxel-Lighting/commit/4d09011dea566c657f39b66cd64318572b289621))
* main should be part of preview builds ([#46](https://github.com/fresolina/Unity-Voxel-Lighting/issues/46)) ([02776ca](https://github.com/fresolina/Unity-Voxel-Lighting/commit/02776caf41e215c4d1ca0889b8c8ed7d8ff45aea))
* Move samples scripts ([3ea15c6](https://github.com/fresolina/Unity-Voxel-Lighting/commit/3ea15c60c7cbed71f1b0955de440be15631839d9))
* Remove _Samples from git ([e6bfaac](https://github.com/fresolina/Unity-Voxel-Lighting/commit/e6bfaacd0f159aeb6d76cd70d09c555cc52916a2))

## [1.2.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.1.0...v1.2.0) (2026-04-04)


### Features

* setup demo project ([4123d16](https://github.com/fresolina/Unity-Voxel-Lighting/commit/4123d16c99cf02598ec37848731b164f68a3d357))

## [1.1.0](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.0.1...v1.1.0) (2026-03-28)


### Features

* Web support via WebGPU ([435995e](https://github.com/fresolina/Unity-Voxel-Lighting/commit/435995eba60e24b60502f6687c75b67d20d0eeb4))

## [1.0.1](https://github.com/fresolina/Unity-Voxel-Lighting/compare/v1.0.0...v1.0.1) (2026-03-20)


### Bug Fixes

* todo ([336b193](https://github.com/fresolina/Unity-Voxel-Lighting/commit/336b19377064964dac1be45c1044ea541ada0226))

## 1.0.0 (2026-03-20)


### Features

* Blur irradiance final field, optionally stop calculations after gi field is considered stable ([093a94c](https://github.com/fresolina/Unity-Voxel-Lighting/commit/093a94c8871ee402aaa5106b40ec14bb0139dafb))
* GI calculation by slow temporal convergence ([24ba263](https://github.com/fresolina/Unity-Voxel-Lighting/commit/24ba263f0c24f807231ab3a1b69fac1f7c3957be))
* occlusion direction bitmask shadows ([b4eca67](https://github.com/fresolina/Unity-Voxel-Lighting/commit/b4eca67b9d863d890c9d16c55620ef115d5c5c40))
* occlusion-direction-bitmask-shadows ([#4](https://github.com/fresolina/Unity-Voxel-Lighting/issues/4)) ([4870d3d](https://github.com/fresolina/Unity-Voxel-Lighting/commit/4870d3db6fbb30b2dd464b75602eb73194288ed1))
* SDF AO ([1860d7d](https://github.com/fresolina/Unity-Voxel-Lighting/commit/1860d7d01174d54fd90b4c9eee57ca3e8526546f))
* SDF ray marching shadows ([df727ca](https://github.com/fresolina/Unity-Voxel-Lighting/commit/df727ca4ebccf17b420dfe0ab99c070016bad481))
* **sdf:** Support plane geometry ([07f301d](https://github.com/fresolina/Unity-Voxel-Lighting/commit/07f301d72d1ca6d827191ba1ba8037c79e78d9aa))
* soft raymarching shadows ([adaa3f9](https://github.com/fresolina/Unity-Voxel-Lighting/commit/adaa3f92b7287fb01796fabd40af1a1e604089df))
* support emission and LPV GI mode ([6cb9761](https://github.com/fresolina/Unity-Voxel-Lighting/commit/6cb9761978e2fc128891efa448478d4775aca6a7))


### Bug Fixes

* **editor:** Ensure Volume is reset dependent classes ([03b30ca](https://github.com/fresolina/Unity-Voxel-Lighting/commit/03b30ca64f69cb8fcc6790f838fc2509ebefabc8))
* Recompute volume every bake and only bake static active objects ([7860071](https://github.com/fresolina/Unity-Voxel-Lighting/commit/7860071feed863f8ff86a577da3439c60a5bd77b))
* reduce light leak in corners ([f8cebe7](https://github.com/fresolina/Unity-Voxel-Lighting/commit/f8cebe72580e10d7bf44c91f612af4d073245f36))
* **SDF:** Don't bake inactive gameobjects ([5352835](https://github.com/fresolina/Unity-Voxel-Lighting/commit/53528353a112059491a1b7bce49b3344de0f72a2))
* **SDF:** Remove artifacts by skipping bad triangles ([7f1b371](https://github.com/fresolina/Unity-Voxel-Lighting/commit/7f1b3719df2d58317cec43146ab1cb24aa51001e))
* Use Sky as ambient light ([e2704ee](https://github.com/fresolina/Unity-Voxel-Lighting/commit/e2704eee9dec88387340b5057e7cd4a893ff806c))

## 1.0.0 (2024-12-07)


### Features

* add editorconfig and update readme ([b3f530a](https://github.com/fresolina/unity-package-template/commit/b3f530ac34dacfc5fb4352c87d34492d881a5298))

## Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
