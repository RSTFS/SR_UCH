import io, os, re, sys, glob

roots = [
    (r"D:\RS_TFS\CC\SR_UCH\SR_UCH\SR.Core.cs",),
]

pat = re.compile(r'LocDesc\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"', re.S)

out = io.open(r"D:\RS_TFS\CC\SR_UCH\_tips_dump.txt", "w", encoding="utf-8", newline="\n")
targets = sys.argv[1:]
for path in targets:
    t = io.open(path, encoding="utf-8").read()
    for m in pat.finditer(t):
        ln = t.count("\n", 0, m.start()) + 1
        sec, key, zh, en = m.group(1), m.group(2), m.group(3), m.group(4)
        out.write("--- %s:%d  [%s] %s\n" % (os.path.basename(path), ln, sec, key))
        for k, v in (("zh", zh), ("en", en)):
            parts = v.split("\\n")
            out.write("    %s(%d): %s\n" % (k, len(parts), " | ".join(p.strip() for p in parts)))
out.close()
print("ok")
