"""Verify actual focused OrderStatus executions and immutable raw evidence."""
import collections
import hashlib
import json
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

EXPECTED = {
    "OrderStatusSourceHttpTests": {
        "ExactUtf16BoundaryPersists_OneUnitOverflowDoesNotWrite": 8,
        "NullNameIsRejectedWithoutChangingRowsOrTimestamps": 2,
        "NullDescriptionAndEmptyOrPaddedStringsPreserveTheirExactValues": 8,
        "AuthenticationAndPermissionPrecedeInvalidInputWithoutWrites": 4,
        "MissingPutRemainsNotFoundBeforeNullOrOverflowValidation": 2,
    },
    "OrderStatusSourceMigrationTests": {
        "ModelAndSnapshotRetainExactSourceStringsAndChecks": 1,
        "PhysicalUtf16LimitsPreserveExactTrailingSpacesAndRejectOverflow": 4,
        "NullOnlyRequirednessAllowsEmptyPaddingAndNullableDescription": 4,
        "PhysicalNullNameRefusesWithoutChangingAnyRetainedState": 1,
        "RetainedInvalidSourceRowsRefuseUpgradeBeforeAnySchemaOrHistoryChange": 3,
        "RowAdmissionRejects10001ThenAccepts10000WithoutRewritingRows": 1,
        "UnexpectedPreimageOrNamedCheckCollisionRefusesAtomicUpgrade": 6,
        "UpDownUpPreservesEntireStatusGraphAndRestoresExactPreimageMetadata": 1,
        "MissingOrWrongOwnedCheckRefusesDowngradeAndRollsBackTempGuard": 4,
        "WriterLockDeadlineRefusesUpgradeAndReleasesMigrationTransaction": 1,
    },
}
MAX_BYTES = 64 * 1024 * 1024
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

def read(path):
    for part in (path, *path.parents):
        assert not part.is_symlink() and not part.is_junction(), "Linked evidence refused"
    assert path.stat().st_size <= MAX_BYTES, "Evidence size exceeds limit"
    with path.open("rb") as stream:
        value = stream.read(MAX_BYTES + 1)
    assert len(value) <= MAX_BYTES and b"<!DOCTYPE" not in value, "Unsafe evidence refused"
    return value

def verify(root):
    files = []
    entries = 0
    for folder, directories, names in os.walk(root, followlinks=False):
        entries += len(directories) + len(names)
        assert entries <= 256, "Evidence inventory exceeds limit"
        for name in directories:
            path = Path(folder) / name
            assert not path.is_symlink() and not path.is_junction(), "Linked inventory refused"
        files.extend(Path(folder) / name for name in names)
    assert sum(path.stat().st_size for path in files) <= 256 * 1024 * 1024, "Evidence aggregate exceeds limit"
    reports = [path for path in files if path.suffix == ".trx"]
    assert len(reports) == 1, "Expected exactly one TRX"
    content = read(reports[0])
    document = ET.fromstring(content)
    definitions = document.findall("./t:TestDefinitions/t:UnitTest", NS)
    ids = [node.get("id") for node in definitions]
    assert all(ids) and len(set(ids)) == len(ids), "Invalid test definition identities"
    methods = {node.get("id"): node.find("t:TestMethod", NS) for node in definitions}
    results = document.findall("./t:Results/t:UnitTestResult", NS)
    execution_ids = [node.get("executionId") for node in results]
    assert all(execution_ids) and len(set(execution_ids)) == len(execution_ids), "Duplicate execution identity"
    actual = collections.Counter()
    identities = set()
    for node in results:
        assert node.get("outcome") == "Passed" and node.get("testId") in methods, "Nonpassing or unknown execution"
        method = methods[node.get("testId")]
        assert method is not None
        qualified = method.get("className", "").split(",")[0]
        assert qualified in ("Legacy.Maliev.OrderService.Tests.Controllers.OrderStatusSourceHttpTests", "Legacy.Maliev.OrderService.Tests.Data.OrderStatusSourceMigrationTests"), "Wrong focused class identity"
        entity = qualified.rsplit(".", 1)[-1]
        name = method.get("name")
        assert entity in EXPECTED and name in EXPECTED[entity], "Unknown focused execution"
        identity = (qualified, name, node.get("testName"))
        assert identity[2] and identity not in identities, "Duplicate case identity"
        identities.add(identity)
        actual[(entity, name)] += 1
    expected = {(entity, name): count for entity, names in EXPECTED.items() for name, count in names.items()}
    assert dict(actual) == expected, "Focused cardinality drift"
    counters = document.find("./t:ResultSummary/t:Counters", NS)
    assert counters is not None
    assert all(int(counters.get(key, "-1")) == 50 for key in ("total", "executed", "passed"))
    failures = ("failed", "error", "timeout", "aborted", "inconclusive", "notExecuted", "disconnected", "warning", "passedButRunAborted")
    assert all(key in counters.attrib for key in failures), "Missing native failure counters"
    assert all(int(counters.get(key)) == 0 for key in failures)
    raw = [read(path) for path in files if path.name == "coverage.cobertura.xml"]
    assert raw and len({hashlib.sha256(value).hexdigest() for value in raw}) == 1, "Raw coverage absent or mismatched"
    packages = ET.fromstring(raw[0]).findall("./packages/package")
    for assembly in ("Api", "Application", "Data", "Domain"):
        matches = [node for node in packages if node.get("name") == "Legacy.Maliev.OrderService." + assembly]
        assert matches and any(node.findall(".//line") for node in matches), "Missing owned coverage inventory"
    return {"passed": 50, "failed": 0, "skipped": 0, "methods": EXPECTED,
            "trxSha256": hashlib.sha256(content).hexdigest(), "rawSha256": hashlib.sha256(raw[0]).hexdigest(),
            "rawCopies": len(raw), "head": os.environ.get("GITHUB_SHA"),
            "note": "Focused evidence only. Full suite must retain all prior324 cases and gate all four assemblies at80 without exclusions; persistentDDL is not accepted."}

if __name__ == "__main__":
    root = Path(sys.argv[1]).absolute()
    receipt = verify(root)
    (root / "order-status-source-proof.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))
