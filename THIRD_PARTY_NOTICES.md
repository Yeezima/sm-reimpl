# Third-party notices

sm-reimpl is licensed under GPL-3.0 (see `LICENSE`). It includes or depends on the following third-party work, used
under the licences below. None of it is game content.

## ww2ogg (port; codebook library downloaded, not distributed)

- Source: https://github.com/hcs64/ww2ogg (version 0.24, tag `0.24`, commit `4b4ca8ec4079a42488aae6344c3c52165e210eec`),
  by Adam Gashlin (hcs).
- Used in: `src/SmFormats/Audio/WwiseVorbis.cs` is a C# port of `src/wwriff.cpp`, `src/codebook.cpp` and
  `src/Bit_stream.h` (Wwise Vorbis to standard Vorbis).
- Codebook library: ww2ogg's `packed_codebooks.bin` holds Vorbis codebooks in Wwise's stripped form (data that
  originates with Audiokinetic's Wwise and was recovered by ww2ogg), so its licence may not be ww2ogg's alone. It is
  **not in this repository or any build**. `SmContent.AudioCache` downloads it on first use from
  `https://raw.githubusercontent.com/hcs64/ww2ogg/4b4ca8ec4079a42488aae6344c3c52165e210eec/packed_codebooks.bin`
  into `%LOCALAPPDATA%\sm-reimpl\cache\audio-codebooks\` and keeps it only if its SHA-256 is
  `442e9442aad0bbd263df4d721d6747a74d440c899978b1fab984bcbf455fd592` (74,164 bytes).
- Licence of the ported code: BSD-3-Clause (ww2ogg `COPYING`, checked 2026-10-27), reproduced here:

```
Copyright (c) 2002, Xiph.org Foundation
Copyright (c) 2009-2016, Adam Gashlin

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## NVorbis (NuGet package, tools and tests only)

- Source: https://github.com/NVorbis/NVorbis, package `NVorbis` 0.10.5.
- Used in: `src/SmTool` (decoding check and WAV output of `smtool audio-decode`) and `tests/SmFormats.Tests`. Not used
  by the server or the client.
- Licence: MIT (the package's `LICENSE` file), reproduced here:

```
MIT License

Copyright (c) 2020 Andrew Ward

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

## Loyalist bark voice pack (the maintainer's own assets; added content)

- What: 32 OGG Vorbis voice takes (8 phrases x 4) and a slimmed manifest in `client/Extras/LoyalistBarks/assets/`, embedded in the client assembly only when
  added content is built (`docs/added-content.md` section 7).
- Provenance: the maintainer states that the takes were made on 2026-10-29 with ElevenLabs (model eleven_v4, voice "SM-Reimpl Loyalist - Voice 1") using a
  custom voice they created in ElevenLabs, which is not cloned from any game or person and is not a shared-library voice; then processed with their helmet
  filter (`helmet_v1`, FFmpeg) and normalised toward -16 LUFS, and that they are synthesized performances, not recordings or extracts of any game. The generation
  records (prompts, generation ids, WAV masters, dry takes) stay with the maintainer, outside the repository.
- Terms: the maintainer's assets; the maintainer states they were generated on a paid ElevenLabs plan and that the maintainer holds the rights to use and distribute them.
- Licence of the audio: released by the maintainer with this project under the GPL-3.0.

## Consulted

- PredatorCZ/HavokLib (https://github.com/PredatorCZ/HavokLib), GPL-3.0. The POLAR32 and THREECOMP40 rotation decoders in
  `src/SmFormats/Havok/HavokSplineAnimation.cs` were written independently; their bit layouts and constants follow HavokLib's
  documented encodings, and the THREECOMP40 sign bit was then chosen on the game's data (docs/discovery.md, "Havok packfiles").
  Its licence is the same as this project's.
- wwiser (https://github.com/bnnm/wwiser): no licence, as stated by the project at the time of consultation. Its documentation of
  bank version 56 field names was consulted; every layout in `src/SmFormats/Audio/SoundBank.cs` was written anew and checked against
  the install (docs/discovery.md, "Audio (Wwise)"). No wwiser code is used.
- GameExtractor (https://github.com/wattostudios/GameExtractor, `Plugin_OPPC_OBPK.java`; GPL-2.0+, as stated by the project at the
  time of consultation) and aluigi's QuickBMS script `warhammer_40000_oppc.bms` v0.2 were read as format evidence for the OPPC / OBPK
  archive layout (docs/discovery.md, "OPPC / OBPK archives"). No code was copied; `src/SmFormats/Obpk` is an independent implementation.
