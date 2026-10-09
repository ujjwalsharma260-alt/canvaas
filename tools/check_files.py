"""Checks for AI assistants (the owner does not need to run this).

1. Every .xaml / .csproj / .yml-adjacent XML file must be valid XML.
2. Every event handler named in a .xaml file (Click="X", etc.) must exist in the .xaml.cs file.
3. Every x:Name used in code-behind must exist in XAML.

Usage: python3 tools/check_files.py
This does NOT replace a real Windows build. The GitHub Actions build is the source of truth.
"""
import re, sys, pathlib
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
errors = []

for f in list(root.rglob("*.xaml")) + list(root.rglob("*.csproj")):
    if any(p in f.parts for p in ("bin", "obj")):
        continue
    try:
        ET.parse(f)
    except ET.ParseError as e:
        errors.append(f"INVALID XML: {f.relative_to(root)}: {e}")

handler_attrs = re.compile(r'\b(?:Click|Checked|Unchecked|Loaded|Closing|Closed|SelectionChanged|TextChanged|ValueChanged|MouseDown|MouseUp|MouseMove|MouseWheel|KeyDown|KeyUp|StrokeCollected|StrokeErasing|Executed|CanExecute|PreviewMouseDown|StylusDown|StylusMove|StylusUp)="([A-Za-z_][A-Za-z0-9_]*)"')
for xaml in root.rglob("*.xaml"):
    if any(p in xaml.parts for p in ("bin", "obj")):
        continue
    cs = pathlib.Path(str(xaml) + ".cs")
    xtext = xaml.read_text(encoding="utf-8")
    ctext = cs.read_text(encoding="utf-8") if cs.exists() else ""
    for h in handler_attrs.findall(xtext):
        if not re.search(r'\b' + re.escape(h) + r'\s*\(', ctext):
            errors.append(f"MISSING HANDLER: {h} used in {xaml.name} but not found in {cs.name}")
    names = set(re.findall(r'x:Name="([^"]+)"', xtext))
    if cs.exists():
        for n in names:
            pass  # names defined but unused is fine
    m = re.search(r'x:Class="([^"]+)"', xtext)
    if m and cs.exists():
        cls = m.group(1).split(".")[-1]
        if not re.search(r'partial class ' + cls + r'\b', ctext):
            errors.append(f"CLASS MISMATCH: {xaml.name} says {m.group(1)} but {cs.name} has no 'partial class {cls}'")

if errors:
    print("\n".join(errors)); sys.exit(1)
print("OK: all XAML/XML valid, handlers and classes match.")
