# Third-party notices

linuxcloth packages contain or redistribute the following third-party works.
Version selection is locked by `packages.lock.json`, `Directory.Packages.props`,
and the exact SDK in `global.json`; the package file manifest identifies the
exact shipped bytes.

## .NET runtime and libraries

The self-contained Linux applications and Windows GuestBridge include Microsoft
.NET runtime and library components under the MIT License. The distribution also
ships the authoritative .NET `LICENSE.txt` and `ThirdPartyNotices.txt` files.

Project: https://github.com/dotnet/runtime

## Avalonia and managed UI dependencies

Avalonia, MicroCom.Runtime, and Tmds.DBus.Protocol are distributed under the MIT
License. The Avalonia dependency graph also includes SkiaSharp and HarfBuzzSharp;
their package notices and the .NET third-party notice inventory govern the exact
native components included by the selected runtime identifier.

Projects:

- https://github.com/AvaloniaUI/Avalonia
- https://github.com/mono/SkiaSharp
- https://github.com/harfbuzz/harfbuzz
- https://github.com/tmds/Tmds.DBus

Authoritative licenses and notices are installed below
`/usr/share/licenses/linuxcloth/third-party`. They include Avalonia's complete
NOTICE inventory, the exact SkiaSharp/HarfBuzzSharp native-package notices,
MicroCom and Tmds.DBus.Protocol licenses, and the Inter font's SIL OFL 1.1 text.

Representative MIT copyright notices retained here for discoverability:

- Copyright (c) .NET Foundation and Contributors
- Copyright (c) AvaloniaUI OÜ
- Copyright (c) 2015-2016 Xamarin, Inc.
- Copyright (c) 2017-2018 Microsoft Corporation
- Copyright (c) 2021 Nikita Tsukanov
- Copyright 2006 Alp Toker and other Tmds.DBus contributors

## TableClothCatalog

The unmodified official TableCloth catalog snapshot, including catalog data and
images, is redistributed under the Apache License 2.0. Its authoritative
`LICENSE` file is included both beside the catalog and in the package license
directory.

Project: https://github.com/yourtablecloth/TableClothCatalog

## MIT License text

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## System dependencies not redistributed by linuxcloth

QEMU, OVMF/edk2, swtpm, virt-viewer, passt, Bubblewrap, xorriso, and wimlib are
resolved from the host distribution. They are not copied into the linuxcloth
package; their distribution packages carry their own license notices. Windows,
virtio-win media, product keys, generated VM images, TPM state, and UEFI variable
state are not linuxcloth package contents.
