"""Prints a fingerprint of everything a plugin DLL references outside itself
(assembly names, type refs, member refs with signatures; versions ignored).
Two builds with the same fingerprint bind to exactly the same game/BepInEx API at runtime.
usage: python3 ci/refsig.py <dll> [--list]"""
import hashlib, sys
import dnfile

def sig(path):
    md = dnfile.dnPE(path).net.mdtables
    asm = sorted(str(a.Name) for a in md.AssemblyRef.rows)
    def tname(t):
        scope = getattr(t.ResolutionScope, "row", None)
        owner = str(getattr(scope, "Name", "")) if scope is not None and hasattr(scope, "Name") else ""
        return "%s:%s.%s" % (owner, t.TypeNamespace, t.TypeName)
    types = sorted(tname(t) for t in md.TypeRef.rows)
    members = []
    for m in md.MemberRef.rows:
        c = m.Class.row
        owner = "%s.%s" % (getattr(c, "TypeNamespace", ""), getattr(c, "TypeName", "<spec>"))
        raw = m.Signature.value if hasattr(m.Signature, "value") else m.Signature
        members.append("%s::%s %d" % (owner, m.Name, len(bytes(raw))))
    return ["asm " + a for a in asm] + ["type " + t for t in types] + ["member " + x for x in sorted(members)]

lines = sig(sys.argv[1])
if "--list" in sys.argv: print("\n".join(lines))
print("refsig %s (%d refs)" % (hashlib.sha256("\n".join(lines).encode()).hexdigest()[:16], len(lines)))
