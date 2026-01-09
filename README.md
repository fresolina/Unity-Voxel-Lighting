# Template for a new package

Boilerplate template package. Needs some customization for each new package.
Do not open directly in Unity, but rather include this as a package.

## Setup package repo

* Click "Use this template" button and create a new repo.
* Config package.json: Replace examples with dependencies and names.

## Setup Unity project used for developing this package

* Create a new Unity project and open it. Call it something like "Develop package-name".
* Open Package Manager window,  and add this package with "install from disk".
* Unity will not show Samples~ directory, so you may want to create a symlink to it called for example _Samples.
  * On Windows, use mklink /D _Samples Samples~
  * On Mac/Linux, use ln -s Samples~ _Samples
* In VSCode, open "Develop package-name" folder, then add the package folder to workspace. This will ensure VSCode intellisense works for both the project and the package.
