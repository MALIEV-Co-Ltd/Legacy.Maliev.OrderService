"""Verify hosted nullable-sort cases and raw evidence without local .NET execution."""
import collections
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

expected = {
    "AscendingDates_SourceNullFirstThroughNamedAndNumericHttp": 12,
    "AscendingDates_StableIdentifierTiesBeforePaging": 6,
    "DescendingAndDefault_KeepExistingNullLastAndIdentifierDirection": 3,
    "SortedRead_AnonymousAndWrongPermissionCannotDiscloseOrMutate": 3,
}
root = Path(sys.argv[1])
reports = list(root.rglob("*.trx"))
if len(reports) != 1:
    raise SystemExit("Require one actual nullable-sort TRX")
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
trx = ET.parse(reports[0])
actual = collections.Counter()
for result in trx.findall("./t:Results/t:UnitTestResult", ns):
    matches = [method for method in expected if
               f"OrderNullableSortSourceHttpTests.{method}" in result.get("testName", "")]
    if len(matches) != 1 or result.get("outcome") != "Passed":
        raise SystemExit("Unexpected or non-passing sort case")
    actual[matches[0]] += 1
if dict(actual) != expected:
    raise SystemExit(f"Sort cardinality mismatch: {dict(actual)}")
counters = trx.find("./t:ResultSummary/t:Counters", ns)
if counters is None or any(int(counters.get(key, "-1")) != 24 for key in ("total", "executed", "passed")):
    raise SystemExit("Require exactly 24 executed/passed cases")
if any(int(counters.get(key, "0")) != 0 for key in
       ("failed", "error", "timeout", "aborted", "inconclusive", "notExecuted")):
    raise SystemExit("Failed or skipped sort case")
raw = list(root.rglob("coverage.cobertura.xml"))
digests = {hashlib.sha256(path.read_bytes()).hexdigest() for path in raw}
if len(digests) != 1:
    raise SystemExit("Require one unique raw report")
packages = ET.parse(raw[0]).findall("./packages/package")
for assembly in ("Api", "Application", "Data", "Domain"):
    selected = [p for p in packages if p.get("name") == "Legacy.Maliev.OrderService." + assembly]
    if not selected or not any(p.findall(".//line") for p in selected):
        raise SystemExit(f"Missing owned executable inventory: {assembly}")
proof = {
    "passed": 24, "failed": 0, "skipped": 0, "methods": dict(actual),
    "trxSha256": hashlib.sha256(reports[0].read_bytes()).hexdigest(),
    "rawSha256": next(iter(digests)), "rawCopies": len(raw),
    "fullServiceCoverageAcceptance": False, "actualAuthProducerAcceptance": False,
    "note": "Focused sort proof only. Order/deletion-intent/history snapshots do not claim all tables. Full coverage and external authority/atomic graphs remain separate.",
}
(root / "nullable-sort-proof.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
print(json.dumps(proof, indent=2))
