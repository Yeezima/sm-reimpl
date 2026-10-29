

---

# Components inside the binary packages

The sections above come from the repository's `THIRD_PARTY_NOTICES.md`. The binary packages also contain the following
runtimes. None of them is game content.

## Godot Engine 4.7.2 (the game, in the playtest zip only)

The game is exported with Godot Engine (.NET build), https://godotengine.org. Licence: MIT.
Godot also contains third-party components under their own licences; the list is at https://godotengine.org/license
and in the engine's `COPYRIGHT.txt` (https://github.com/godotengine/godot/blob/4.7.2-stable/COPYRIGHT.txt; in the playtest zip:
`notes/licenses/godot/`).

```
Copyright (c) 2014-present Godot Engine contributors.
Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## .NET runtime (self-contained in the game, the servers and the launcher)

The packages carry the .NET runtime of the version named in `VERSION.txt` so that no .NET installation is needed.
The launcher (`0 - SM Reimpl Launcher.exe` in the playtest zip) also carries the .NET Windows Desktop runtime (Windows Forms) of the
same version, licence MIT, Copyright (c) .NET Foundation and Contributors (in the playtest zip: `notes/licenses/dotnet-windowsdesktop/`;
https://github.com/dotnet/winforms).
Licence: MIT, Copyright (c) .NET Foundation and Contributors. The runtime's own licence and third-party notices are in
the `licenses/dotnet/` folder of each package (in the playtest zip: `notes/licenses/dotnet/`; `LICENSE.TXT`,
`THIRD-PARTY-NOTICES.TXT`); the same files are at https://github.com/dotnet/runtime.
