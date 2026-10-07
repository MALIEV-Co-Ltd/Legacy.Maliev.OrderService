import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import run_commerce_source_intake_v1 as entry


class EntrypointControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.baseline = Path(os.environ['COMMERCE_BASELINE'])
        cls.here = Path(entry.__file__).resolve().parent

    def invoke(self, destination, baseline=None):
        stream = io.StringIO()
        with patch('sys.argv', ['intake', '--baseline', str(baseline or self.baseline),
                                '--destination', str(destination), '--local-capsule']), contextlib.redirect_stdout(stream):
            entry.main()
        return json.loads(stream.getvalue())

    def test_actual_capsule_assembles_exact_frozen_postimages_without_git_or_native_acceptance(self):
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / 'source'
            result = self.invoke(destination)
            self.assertFalse(result['sdkStarted'])
            self.assertFalse(result['nativeAccepted'])
            self.assertFalse(result['gitAncestryCopied'])
            self.assertFalse((destination / 'candidate/.git').exists())
            policy = json.loads((self.here / 'policy.json').read_bytes())
            shared = entry.adapter.load_shared(self.here / 'sealed_source_capsule.py', entry.adapter.SHARED_SHA256,
                (self.here / 'policy.json').read_bytes(), entry.POLICY_SHA256, entry.LANE)
            files = shared.validate_zip((self.here / 'capsule.zip').read_bytes(), policy['bundleSha256'],
                                        policy['bundleBytes'], policy['entries'])
            manifest, dependencies = entry.adapter.bind(shared, files, policy)
            replacements = {row['path']: row['sha256'] for row in manifest['files']}
            expected = {row['path']: replacements.get(row['path'], row['sha256']) for row in policy['baseFiles']}
            expected.update(replacements)
            actual = {str(path.relative_to(destination / 'candidate')).replace('\\', '/'):
                      hashlib.sha256(path.read_bytes()).hexdigest()
                      for path in (destination / 'candidate').rglob('*') if path.is_file()}
            self.assertEqual(expected, actual)
            actual_dependencies = {str(path.relative_to(destination / 'dependencies')).replace('\\', '/'):
                hashlib.sha256(path.read_bytes()).hexdigest()
                for path in (destination / 'dependencies').rglob('*') if path.is_file()}
            self.assertEqual({row['repository'] + '/' + row['path']: row['sha256'] for row in dependencies['files']},
                             actual_dependencies)

    def test_missing_baseline_refused_before_destination_creation(self):
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / 'source'
            with self.assertRaises(FileNotFoundError):
                self.invoke(destination, Path(temporary) / 'missing')
            self.assertFalse(destination.exists())

    def test_existing_destination_preserves_foreign_sentinel(self):
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary)
            sentinel = destination / 'keep.txt'
            sentinel.write_bytes(b'foreign existing source')
            with self.assertRaises(ValueError):
                self.invoke(destination)
            self.assertEqual(b'foreign existing source', sentinel.read_bytes())
            self.assertEqual(['keep.txt'], sorted(path.name for path in destination.iterdir()))

    def test_wrong_fixed_capsule_object_refused_without_materialization(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(entry, 'CAPSULE_GIT_BLOB', '0' * 40):
            destination = Path(temporary) / 'source'
            with self.assertRaisesRegex(ValueError, 'Borrowed capsule'):
                self.invoke(destination)
            self.assertFalse(destination.exists())

    def invoke_owned(self, parent):
        stream = io.StringIO()
        with patch('sys.argv', ['intake', '--baseline', str(self.baseline),
                                '--owned-root', str(parent), '--local-capsule']), contextlib.redirect_stdout(stream):
            entry.main()
        return json.loads(stream.getvalue())

    def test_owned_workflow_cleanup_preserves_preexisting_foreign_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            foreign = parent / 'commerce-source-only'
            foreign.mkdir()
            sentinel = foreign / 'keep.txt'
            sentinel.write_bytes(b'foreign target must survive workflow cleanup')
            result = self.invoke_owned(parent)
            self.assertTrue(result['resourceCleanup']['exclusiveCreation'])
            self.assertTrue(result['resourceCleanup']['removed'])
            self.assertEqual(b'foreign target must survive workflow cleanup', sentinel.read_bytes())
            self.assertEqual(['commerce-source-only'], sorted(path.name for path in parent.iterdir()))

    def test_owned_workflow_removes_only_its_partial_failed_assembly(self):
        def fail(shared, destination, *arguments):
            destination.mkdir()
            (destination / 'partial.txt').write_bytes(b'partial task-owned assembly')
            raise ValueError('Injected partial assembly failure')
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            foreign = parent / 'commerce-source-only'
            foreign.mkdir()
            sentinel = foreign / 'keep.txt'
            sentinel.write_bytes(b'foreign')
            cleanup_log = io.StringIO()
            with patch.object(entry.adapter, 'materialize', side_effect=fail), contextlib.redirect_stderr(cleanup_log):
                with self.assertRaisesRegex(ValueError, 'Injected partial'):
                    self.invoke_owned(parent)
            self.assertTrue(json.loads(cleanup_log.getvalue())['resourceCleanup']['removed'])
            self.assertEqual(b'foreign', sentinel.read_bytes())
            self.assertEqual(['commerce-source-only'], sorted(path.name for path in parent.iterdir()))


if __name__ == '__main__':
    unittest.main()
