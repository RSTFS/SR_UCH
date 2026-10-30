import io, os, re, sys

pat = re.compile(
    r'LocDesc\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*[,)]',
    re.S)

out = io.open(r"D:\RS_TFS\CC\SR_UCH\_tips_dump.txt", "w", encoding="utf-8", newline="\n")
seen = 0
for path in sys.argv[1:]:
    t = io.open(path, encoding="utf-8").read()
    for m in pat.finditer(t):
        ln = t.count("\n", 0, m.start()) + 1
        arg1, arg2, zh, en = m.group(1).strip(), m.group(2).strip(), m.group(3), m.group(4)
        out.write("--- %s:%d  (%s , %s)\n" % (os.path.basename(path), ln, arg1, arg2))
        for k, v in (("zh", zh), ("en", en)):
            parts = v.split("\\n")
            out.write("    %s(%d): %s\n" % (k, len(parts), " | ".join(p.strip() for p in parts)))
        seen += 1
out.close()
print("entries:", seen)
