# Clipper2 1.5.4

Source: https://github.com/AngusJohnson/Clipper2/tree/Clipper2_1.5.4/CSharp/Clipper2Lib

Copyright Angus Johnson. Distributed under the Boost Software License 1.0 (see LICENSE.txt).
HashCode.cs also carries .NET Foundation MIT and Yann Collet BSD notices; see its source
header and DOTNET-LICENSE.txt. All these notices are embedded in the compiled mod.
The C# library source is compiled into SurfaceNetworkFilter.dll. The only source adjustment
is enabling nullable annotations in Clipper.Offset.cs and HashCode.cs, matching the upstream
project's nullable setting when built inside this project. The notices in HashCode.cs are
also embedded alongside the Boost license in the compiled mod.
There is no additional installed mod or runtime DLL dependency. Only polygon boolean operations
are used; the mod retains its own native area triangulation and deck-height handling.
