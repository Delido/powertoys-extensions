"""Registers an extension class in Package.appxmanifest and Program.cs.

usage: python scripts/add-extension.py <GUID> <Id> <Folder> <ClassName> "<Display Name>" "<Description>"
"""
import pathlib
import sys

guid, ext_id, folder, cls, name, desc = sys.argv[1:7]
root = pathlib.Path(__file__).resolve().parent.parent / "src" / "PowerToysExtensions"

manifest = root / "Package.appxmanifest"
text = manifest.read_text(encoding="utf-8")
if guid in text:
    sys.exit(f"{guid} is already registered")
text = text.replace(
    "            </com:ExeServer>",
    f'              <com:Class Id="{guid}" DisplayName="{name}" />\n            </com:ExeServer>',
    1,
)
block = f"""        <uap3:Extension Category="windows.appExtension">
          <uap3:AppExtension Name="com.microsoft.commandpalette" Id="{ext_id}" PublicFolder="Public"
            DisplayName="{name}" Description="{desc}">
            <uap3:Properties>
              <CmdPalProvider>
                <Activation><CreateInstance ClassId="{guid}" /></Activation>
                <SupportedInterfaces><Commands /></SupportedInterfaces>
              </CmdPalProvider>
            </uap3:Properties>
          </uap3:AppExtension>
        </uap3:Extension>
      </Extensions>"""
text = text.replace("      </Extensions>", block, 1)
manifest.write_text(text, encoding="utf-8", newline="\n")

program = root / "Program.cs"
text = program.read_text(encoding="utf-8")
using = f"using PowerToysExtensions.Extensions.{folder};"
lines = text.split("\n")
usings = [l for l in lines if l.startswith("using PowerToysExtensions.Extensions.")] + [using]
first = next(i for i, l in enumerate(lines) if l.startswith("using PowerToysExtensions.Extensions."))
lines = [l for l in lines if not l.startswith("using PowerToysExtensions.Extensions.")]
lines[first:first] = sorted(set(usings))
text = "\n".join(lines)
# the Register(...) calls end with a blank line before server.Start()
marker = "\n\n        server.Start();"
text = text.replace(marker, f"\n        Register(server, () => new {cls}());{marker}", 1)
program.write_text(text, encoding="utf-8", newline="\n")
print(f"registered {name}")
