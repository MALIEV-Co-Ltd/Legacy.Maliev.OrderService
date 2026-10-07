"""Commerce graph adapter for the shared File capsule intake; never starts an SDK."""
import types
import hashlib
import json
from pathlib import Path

PROFILES = {
    'accounting': ('MALIEV-Co-Ltd/Legacy.Maliev.AccountingService',
                   '668f2cb63c64b911db776329b983dd91944b3b8c', 49,
                   '7b3099bf67d0f17e56cfdb3dcf36541304abaac2'),
    'procurement': ('MALIEV-Co-Ltd/Legacy.Maliev.ProcurementService',
                    '8e34d0208be9312ab0b94dc8db7800d7c12d86a2', 30,
                    'ecb05cbbd68717e415f69df2ac488c1d323b1da3'),
    'order': ('MALIEV-Co-Ltd/Legacy.Maliev.OrderService',
              '4297f695f93f2cb312979b0c0c35bf662e22dd48', 44,
              '7edcd961024868513fd5f373cab3dcb261197f77'),
}
COMPATIBILITY = '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7'
OWNER = '01a1009c-aa47-72d2-9914-3a0784a67c0e'
COORDINATOR = '019fc21e-50f0-7112-834f-9fb3b35b9dfe'
SHARED_SHA256 = '44a8a5accac9da11422d606be02fe28487642215df511b5f1c4284296a453ee2'


def load_shared(path, expected_sha256, policy_raw, expected_policy_sha256, lane):
    """Only the independently reviewed policy may supply this producer seal."""
    if len(policy_raw) > 16 * 1024 * 1024 or hashlib.sha256(policy_raw).hexdigest() != expected_policy_sha256:
        raise ValueError('Exact caller policy must be bound before executing shared code')
    validate_profile(json.loads(policy_raw), lane)
    if expected_sha256 != SHARED_SHA256:
        raise ValueError('Only independently reviewed shared producer bytes are allowed')
    path = Path(path).absolute()
    if any(part.is_symlink() or getattr(part.lstat(), 'st_file_attributes', 0) & 0x400 for part in (path, *path.parents)):
        raise ValueError('Linked shared implementation refused')
    with path.open('rb') as stream:
        raw = stream.read(256 * 1024 + 1)
    if len(raw) > 256 * 1024 or hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise ValueError('Shared producer seal differs')
    module = types.ModuleType('sealed_source_capsule')
    module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec'), module.__dict__)
    return module


def policy(shared, raw, expected_sha256, lane):
    if shared.digest(raw) != expected_sha256 or lane not in PROFILES:
        raise ValueError('Exact independently reviewed lane policy required')
    value = shared.parse_json(raw)
    validate_profile(value, lane)
    return value


def validate_profile(value, lane):
    if lane not in PROFILES:
        raise ValueError('Unknown Commerce lane')
    repository, base, count, defaults = PROFILES[lane]
    if type(value.get('schemaVersion')) is not int or value['schemaVersion'] != 1:
        raise ValueError('Unsupported adapter policy')
    if (value.get('repository'), value.get('acceptedBase'), value.get('candidateFileCount')) != (repository, base, count):
        raise ValueError('Borrowed candidate profile')
    if value.get('owner') != OWNER or value.get('coordinator') != COORDINATOR:
        raise ValueError('Foreign graph owner')
    if value.get('executionMode') != 'source-intake-only' or value.get('sdkAuthorized') is not False:
        raise ValueError('Intake cannot authorize native execution')
    if value.get('dependencyPins') != {'Legacy.Maliev.ServiceDefaults': defaults,
                                      'Legacy.Maliev.CompatibilityContracts': COMPATIBILITY}:
        raise ValueError('Exact lane dependencies required')


def bind(shared, files, value):
    manifest_raw = files['candidate/manifest.json']
    if shared.digest(manifest_raw) != value['candidateManifestSha256']:
        raise ValueError('Candidate manifest differs')
    manifest = shared.parse_json(manifest_raw)
    if manifest.get('base') != value['acceptedBase'] or len(manifest['files']) != value['candidateFileCount']:
        raise ValueError('Candidate base/inventory differs')
    paths = set()
    for row in manifest['files']:
        path = shared.canonical_path(row['path'])
        if path in paths or shared.digest(files['candidate/raw/' + path]) != row['sha256']:
            raise ValueError('Duplicate or mismatched candidate postimage')
        paths.add(path)
    dependency_raw = files['dependency-manifest.json']
    if shared.digest(dependency_raw) != value['dependencyManifestSha256']:
        raise ValueError('Dependency manifest differs')
    dependencies = shared.parse_json(dependency_raw)
    if dependencies['pins'] != value['dependencyPins']:
        raise ValueError('Dependency revisions differ')
    dependency_paths = set()
    for row in dependencies['files']:
        repository = row['repository']
        if repository not in value['dependencyPins']:
            raise ValueError('Foreign dependency source')
        path = shared.canonical_path(repository + '/' + row['path'])
        if path in dependency_paths or shared.digest(files['dependencies/' + path]) != row['sha256']:
            raise ValueError('Duplicate or mismatched dependency source')
        dependency_paths.add(path)
    expected = {'candidate/manifest.json', 'dependency-manifest.json'}
    expected.update('candidate/raw/' + path for path in paths)
    expected.update('dependencies/' + path for path in dependency_paths)
    if set(files) != expected:
        raise ValueError('Unreviewed capsule entries')
    return manifest, dependencies


def materialize(shared, destination, base_source, files, value):
    """The owner first binds every baseline raw Git blob to its accepted base policy."""
    root = Path(destination).absolute()
    shared.reject_links(root)
    if root.exists():
        raise ValueError('Fresh isolated destination required')
    expected = {row['path']: row for row in value['baseFiles']}
    if len(expected) != len(value['baseFiles']) or set(base_source) != set(expected):
        raise ValueError('Incomplete or ambiguous accepted baseline')
    for path, raw in base_source.items():
        shared.canonical_path(path)
        if len(raw) != expected[path]['bytes'] or shared.digest(raw) != expected[path]['sha256']:
            raise ValueError('Accepted baseline raw Git blob differs')
    manifest, dependencies = bind(shared, files, value)
    replacements = {row['path'] for row in manifest['files']}
    root.mkdir(parents=True)
    for path, raw in base_source.items():
        if path not in replacements:
            shared.write_new(root / 'candidate', path, raw)
    for row in manifest['files']:
        shared.write_new(root / 'candidate', row['path'], files['candidate/raw/' + row['path']])
    for row in dependencies['files']:
        path = row['repository'] + '/' + row['path']
        shared.write_new(root / 'dependencies', path, files['dependencies/' + path])
    return {'state': 'SourceMaterialized', 'repository': value['repository'],
            'acceptedBase': value['acceptedBase'], 'candidateFiles': len(replacements),
            'sdkStarted': False, 'nativeAccepted': False,
            'note': 'Independent worker admission, proxy boundary and complete build/test/static/cleanup gates still required.'}


def fetch_materialize(shared, destination, base_source, value, blob):
    """Use the shared bounded same-repository fetch and extractor, then bind our graph."""
    capsule = shared.fetch_git_blob(value['repository'], blob)
    files = shared.validate_zip(capsule, value['bundleSha256'], value['bundleBytes'], value['entries'])
    return materialize(shared, destination, base_source, files, value)
