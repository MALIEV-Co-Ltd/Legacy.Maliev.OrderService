import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import commerce_source_adapter_v1 as adapter

SHARED = Path(__file__).resolve().parent / 'sealed_source_capsule.py'


class Controls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository, base, count, defaults = adapter.PROFILES['accounting']
        cls.loader_policy = json.dumps({'schemaVersion': 1, 'repository': repository, 'acceptedBase': base,
            'candidateFileCount': count, 'owner': adapter.OWNER, 'coordinator': adapter.COORDINATOR,
            'executionMode': 'source-intake-only', 'sdkAuthorized': False,
            'dependencyPins': {'Legacy.Maliev.ServiceDefaults': defaults, 'Legacy.Maliev.CompatibilityContracts': adapter.COMPATIBILITY}}).encode()
        cls.loader_policy_sha = hashlib.sha256(cls.loader_policy).hexdigest()
        cls.shared = adapter.load_shared(SHARED, adapter.SHARED_SHA256, cls.loader_policy, cls.loader_policy_sha, 'accounting')
    def fixture(self, lane='accounting'):
        repository, base, count, defaults = adapter.PROFILES[lane]
        files = {}
        rows = []
        for number in range(count):
            path = 'synthetic/' + str(number) + '.txt'
            raw = ('control-' + str(number)).encode()
            files['candidate/raw/' + path] = raw
            rows.append({'path': path, 'sha256': self.shared.digest(raw)})
        files['candidate/manifest.json'] = json.dumps({'base': base, 'files': rows}).encode()
        pins = {'Legacy.Maliev.ServiceDefaults': defaults, 'Legacy.Maliev.CompatibilityContracts': adapter.COMPATIBILITY}
        files['dependencies/Legacy.Maliev.ServiceDefaults/synthetic.txt'] = b'dependency'
        files['dependency-manifest.json'] = json.dumps({'pins': pins, 'files': [
            {'repository': 'Legacy.Maliev.ServiceDefaults', 'path': 'synthetic.txt', 'sha256': self.shared.digest(b'dependency')}]}).encode()
        value = {'schemaVersion': 1, 'repository': repository, 'acceptedBase': base, 'candidateFileCount': count,
                 'owner': adapter.OWNER, 'coordinator': adapter.COORDINATOR, 'executionMode': 'source-intake-only',
                 'sdkAuthorized': False, 'dependencyPins': pins,
                 'candidateManifestSha256': self.shared.digest(files['candidate/manifest.json']),
                 'dependencyManifestSha256': self.shared.digest(files['dependency-manifest.json']),
                 'baseFiles': [{'path': 'baseline.txt', 'bytes': 4, 'sha256': self.shared.digest(b'base')}]}
        return value, files
    def test_each_lane_binds_and_materializes_without_sdk(self):
        for lane in adapter.PROFILES:
            with self.subTest(lane=lane), tempfile.TemporaryDirectory() as temporary:
                value, files = self.fixture(lane)
                raw = json.dumps(value).encode()
                value = adapter.policy(self.shared, raw, self.shared.digest(raw), lane)
                target = Path(temporary) / 'new'
                receipt = adapter.materialize(self.shared, target, {'baseline.txt': b'base'}, files, value)
                self.assertFalse(receipt['sdkStarted'])
                self.assertEqual(b'control-0', (target / 'candidate/synthetic/0.txt').read_bytes())
                self.assertEqual(b'base', (target / 'candidate/baseline.txt').read_bytes())
    def test_wrong_policy_bytes_refused(self):
        value, _ = self.fixture()
        with self.assertRaises(ValueError): adapter.policy(self.shared, json.dumps(value).encode(), '0' * 64, 'accounting')
    def test_borrowed_lane_refused(self):
        value, _ = self.fixture('order'); raw = json.dumps(value).encode()
        with self.assertRaises(ValueError): adapter.policy(self.shared, raw, self.shared.digest(raw), 'accounting')
    def test_accounting_dependency_borrowed_by_procurement_refused(self):
        value, _ = self.fixture('procurement'); value['dependencyPins']['Legacy.Maliev.ServiceDefaults'] = adapter.PROFILES['accounting'][3]
        raw = json.dumps(value).encode()
        with self.assertRaises(ValueError): adapter.policy(self.shared, raw, self.shared.digest(raw), 'procurement')
    def test_sdk_permission_refused(self):
        value, _ = self.fixture(); value['sdkAuthorized'] = True; raw = json.dumps(value).encode()
        with self.assertRaises(ValueError): adapter.policy(self.shared, raw, self.shared.digest(raw), 'accounting')
    def test_candidate_raw_mutation_refused(self):
        value, files = self.fixture(); files['candidate/raw/synthetic/0.txt'] = b'changed'
        with self.assertRaises(ValueError): adapter.bind(self.shared, files, value)
    def test_extra_capsule_entry_refused(self):
        value, files = self.fixture(); files['unexpected.txt'] = b'extra'
        with self.assertRaises(ValueError): adapter.bind(self.shared, files, value)
    def test_baseline_raw_mutation_refused_before_destination(self):
        value, files = self.fixture()
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / 'new'
            with self.assertRaises(ValueError): adapter.materialize(self.shared, target, {'baseline.txt': b'evil'}, files, value)
            self.assertFalse(target.exists())
    def test_existing_destination_refused(self):
        value, files = self.fixture()
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(ValueError): adapter.materialize(self.shared, temporary, {'baseline.txt': b'base'}, files, value)
    def test_wrong_shared_producer_bytes_refused(self):
        with self.assertRaises(ValueError): adapter.load_shared(SHARED, '0' * 64, self.loader_policy, self.loader_policy_sha, 'accounting')
    def test_caller_policy_is_bound_before_any_shared_execution(self):
        with patch.object(adapter, 'exec', create=True) as execute:
            with self.assertRaises(ValueError): adapter.load_shared(SHARED, adapter.SHARED_SHA256, self.loader_policy, '0' * 64, 'accounting')
            execute.assert_not_called()
    def test_fetch_uses_fixed_same_repository_and_shared_extractor(self):
        value, files = self.fixture()
        value.update(bundleSha256='reviewed-bundle-sha', bundleBytes=7, entries=['reviewed-entry-index'])
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(self.shared, 'fetch_git_blob', return_value=b'capsule') as fetch, \
                patch.object(self.shared, 'validate_zip', return_value=files) as extract:
            receipt = adapter.fetch_materialize(self.shared, Path(temporary) / 'new', {'baseline.txt': b'base'}, value, 'a' * 40)
            fetch.assert_called_once_with(adapter.PROFILES['accounting'][0], 'a' * 40)
            extract.assert_called_once_with(b'capsule', 'reviewed-bundle-sha', 7, ['reviewed-entry-index'])
            self.assertFalse(receipt['nativeAccepted'])
    def test_shared_extractor_failure_precedes_any_materialization(self):
        value, _ = self.fixture()
        value.update(bundleSha256='reviewed-bundle-sha', bundleBytes=7, entries=[])
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(self.shared, 'fetch_git_blob', return_value=b'capsule'), \
                patch.object(self.shared, 'validate_zip', side_effect=ValueError('Shared extractor refused')):
            destination = Path(temporary) / 'new'
            with self.assertRaises(ValueError): adapter.fetch_materialize(self.shared, destination, {'baseline.txt': b'base'}, value, 'a' * 40)
            self.assertFalse(destination.exists())


if __name__ == '__main__': unittest.main()
