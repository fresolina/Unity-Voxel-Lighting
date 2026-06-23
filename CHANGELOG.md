# Changelog

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
