"""Fixed reviewed lane intake. Produces source only; never executes candidate code."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
from datetime import datetime, timedelta, timezone
import commerce_source_adapter_v1 as adapter
from commerce_lane_policy import LANE, POLICY_SHA256, CAPSULE_GIT_BLOB


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--destination', type=Path)
    target.add_argument('--owned-root', type=Path)
    parser.add_argument('--local-capsule', action='store_true')
    options = parser.parse_args()
    here = Path(__file__).resolve().parent
    # Policy and producer bytes are captured and bound before producer execution.
    raw = (here / 'policy.json').read_bytes()
    shared = adapter.load_shared(here / 'sealed_source_capsule.py', adapter.SHARED_SHA256,
                                 raw, POLICY_SHA256, LANE)
    value = adapter.policy(shared, raw, POLICY_SHA256, LANE)
    if value['capsuleGitBlob'] != CAPSULE_GIT_BLOB:
        raise ValueError('Borrowed capsule Git object')
    baseline = options.baseline.absolute()
    shared.reject_links(baseline)
    base_source = {}
    total = 0
    for row in value['baseFiles']:
        path = shared.canonical_path(row['path'])
        source = baseline / path
        shared.reject_links(source)
        with source.open('rb') as stream:
            postimage = stream.read(shared.MAX_FILE_BYTES + 1)
        total += len(postimage)
        if len(postimage) > shared.MAX_FILE_BYTES or total > 16 * 1024 * 1024:
            raise ValueError('Accepted baseline exceeds bounded source budget')
        if path in base_source:
            raise ValueError('Duplicate accepted baseline path')
        base_source[path] = postimage
    if options.local_capsule:
        with (here / 'capsule.zip').open('rb') as stream:
            capsule = stream.read(shared.MAX_ARCHIVE_BYTES + 1)
        if hashlib.sha1(b'blob ' + str(len(capsule)).encode() + b'\0' + capsule).hexdigest() != CAPSULE_GIT_BLOB:
            raise ValueError('Local same-repository capsule Git object differs')
        files = shared.validate_zip(capsule, value['bundleSha256'], value['bundleBytes'], value['entries'])
    def assemble(destination):
        if options.local_capsule:
            return adapter.materialize(shared, destination, base_source, files, value)
        return adapter.fetch_materialize(shared, destination, base_source, value, CAPSULE_GIT_BLOB)
    if options.owned_root is None:
        result = assemble(options.destination)
    else:
        parent = options.owned_root.absolute()
        shared.reject_links(parent)
        prepared = datetime.now(timezone.utc)
        cleanup = {'owner': adapter.OWNER, 'pid': os.getpid(), 'path': None,
                   'device': None, 'inode': None, 'createdUtc': None,
                   'preparedUtc': prepared.isoformat(),
                   'expiresUtc': (prepared + timedelta(seconds=480)).isoformat(),
                   'persistentData': False, 'exclusiveCreation': False, 'removed': False}
        identity = None
        owned = None
        try:
            # Capture the acquired path before any fallible identity/receipt setup.
            acquired = tempfile.mkdtemp(prefix='commerce-source-only-', dir=parent)
            cleanup['path'] = acquired
            cleanup['exclusiveCreation'] = True
            owned = Path(acquired)
            identity = owned.lstat()
            cleanup['device'], cleanup['inode'] = identity.st_dev, identity.st_ino
            cleanup['createdUtc'] = datetime.now(timezone.utc).isoformat()
            result = assemble(owned / 'source')
        finally:
            try:
                if cleanup['exclusiveCreation']:
                    if identity is None:
                        cleanup['preserved'] = True
                        cleanup['state'] = 'CleanupRequired'
                        cleanup['reason'] = 'Acquired directory identity unavailable; preserve for owner inspection'
                    else:
                        current = owned.lstat()
                        if owned.is_symlink() or (current.st_dev, current.st_ino) != (identity.st_dev, identity.st_ino):
                            cleanup['preserved'] = True
                            cleanup['state'] = 'CleanupRequired'
                            raise ValueError('Owned intake directory identity changed; preserve unknown replacement')
                        shutil.rmtree(owned)
                        cleanup['removed'] = not owned.exists()
                        if not cleanup['removed']:
                            raise ValueError('Owned intake directory remains after cleanup')
            finally:
                # Retain actual cleanup evidence even if assembly or cleanup failed.
                print(json.dumps({'resourceCleanup': cleanup}), file=sys.stderr)
        result['resourceCleanup'] = cleanup
    result['policySha256'] = POLICY_SHA256
    result['capsuleGitBlob'] = CAPSULE_GIT_BLOB
    result['sharedProducerSha256'] = adapter.SHARED_SHA256
    result['gitAncestryCopied'] = False
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
