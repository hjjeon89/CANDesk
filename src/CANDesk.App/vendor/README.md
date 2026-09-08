# vendor/

Native vendor redistributables that CANDesk P/Invokes into but doesn't ship in this repo
(licensing to be confirmed before committing a binary here).

- `vxlapi64.dll` — Vector XL Driver Library (64-bit). Drop the file directly in this folder;
  `CANDesk.App.csproj` copies it to the build output next to `CANDesk.App.exe` automatically
  whenever it's present, so no other setup is needed. Without it, `CANDesk.Hal.Vector`'s
  `VectorCanDeviceFactory.EnumerateAsync` just reports no Vector channels (Vector stays
  disabled in the UI) instead of the app failing to start.
