"""Validate exact source-query fixture bytes and hosted evidence, without running .NET."""
import collections
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

repository = Path(__file__).resolve().parent.parent
expected = {
    "ListSearch_SourceContainsTreatsPercentAndUnderscoreAsLiteral": 6,
    "ListSearch_SourceRetainsSpacesAroundNonemptyLiteralText": 3,
    "ListPage_SourceReturnsNotFoundBeyondLastSelectedRow": 3,
    "ListSearch_KeepsExistingExclusiveIntegerBranch": 9,
    "PendingSearch_KeepsOnlyNameAndDescriptionWhileRegularIncludesOtherFields": 2,
    "LiteralThaiText_RemainsExactThroughHttpAndPostgreSql": 1,
    "MatchingDeletedLifetime_StillConflictsBeforeEmptyPageSelection": 3,
}
fixture = repository / "Legacy.Maliev.OrderService.Tests/Controllers/OrderMasterQuerySourceHttpTests.cs"
text = fixture.read_bytes().decode("utf-8", errors="strict")
thai = r"\u0e0a\u0e34\u0e49\u0e19\u0e07\u0e32\u0e19"
if not text.isascii() or text.count(thai) != 2:
    raise SystemExit("Expected two exact ASCII-escaped Thai literals; fixture encoding changed")
for token in ("Normalpart1001", "detail_assembly", "Fixture%part", "track-only", "comment-only"):
    if token not in text:
        raise SystemExit(f"Missing exact control literal: {token}")
if sys.argv[1] == "--encoding":
    print("Exact UTF-8/ASCII fixture and two escaped Thai literals verified")
    raise SystemExit(0)

root = Path(sys.argv[1])
reports = list(root.rglob("*.trx"))
if len(reports) != 1:
    raise SystemExit(f"Expected one TRX, found {len(reports)}")
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
trx = ET.parse(reports[0])
results = trx.findall(".//t:UnitTestResult", ns)
actual = collections.Counter()
for result in results:
    name = result.attrib["testName"]
    matches = [method for method in expected if
               f"OrderMasterQuerySourceHttpTests.{method}" in name]
    if len(matches) != 1 or result.get("outcome") != "Passed":
        raise SystemExit(f"Unexpected or non-passing query case: {name}")
    actual[matches[0]] += 1
if dict(actual) != expected:
    raise SystemExit(f"Query cardinality mismatch: {dict(actual)}")
counters = trx.find(".//t:Counters", ns)
if counters is None or any(int(counters.get(key, "-1")) != 27
                           for key in ("total", "executed", "passed")):
    raise SystemExit("Expected exactly 27 executed/passed cases")
if any(int(counters.get(key, "0")) != 0 for key in
       ("failed", "error", "timeout", "aborted", "inconclusive", "notExecuted")):
    raise SystemExit("Failed or skipped query case")
raw = list(root.rglob("coverage.cobertura.xml"))
digests = {hashlib.sha256(path.read_bytes()).hexdigest() for path in raw}
if len(digests) != 1:
    raise SystemExit("Expected one unique retained raw coverage report")
packages = ET.parse(raw[0]).findall("./packages/package")
for assembly in ("Api", "Application", "Data", "Domain"):
    name = "Legacy.Maliev.OrderService." + assembly
    selected = [p for p in packages if p.get("name") == name]
    if not selected or not any(p.findall(".//line") for p in selected):
        raise SystemExit(f"Missing executable coverage inventory: {name}")
receipt = {
    "passed": 27, "failed": 0, "skipped": 0, "methods": dict(actual),
    "trx_sha256": hashlib.sha256(reports[0].read_bytes()).hexdigest(),
    "raw_sha256": next(iter(digests)), "raw_copies": len(raw),
    "note": "Focused coverage retained; the full-suite workflow gates all four assemblies at 80%.",
}
(root / "order-query-proof.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
print(json.dumps(receipt, indent=2))
