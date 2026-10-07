import ctypes,hashlib,json,os,shutil,subprocess,tempfile,unittest
from pathlib import Path
from datetime import datetime,timedelta,timezone

ROOT=Path(__file__).resolve().parents[2]
OWNER='order'
SCRIPT=ROOT/'scripts'/f'Render-DormantLegacy{OWNER.title()}Deployment.ps1'
TEMPLATE=ROOT/'deploy/disabled'/f'{OWNER}-deployment.template.yaml'
REPOSITORY=f'asia-southeast1-docker.pkg.dev/maliev-website/offline-fixture/legacy-maliev-{OWNER}-service'
IMAGE=REPOSITORY+'@sha256:'+'1'*64
RESOURCES=[]
CONTROL_COUNT=0

def run_renderer(image=IMAGE,repository=REPOSITORY,account='existing-offline-account',secret='existing-offline-secret',script=SCRIPT):
    global CONTROL_COUNT
    CONTROL_COUNT+=1
    executable=shutil.which('pwsh')
    if not executable:raise RuntimeError('PowerShell7 required for actual offline rendering')
    process=subprocess.Popen([executable,'-NoLogo','-NoProfile','-NonInteractive','-File',str(script),'-Image',image,'-ApprovedImageRepository',repository,'-ExistingServiceAccount',account,'-ExistingRuntimeSecret',secret],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    row={'pid':process.pid,'executableIdentity':executable,'executableSha256':hashlib.sha256(Path(executable).read_bytes()).hexdigest(),'ports':[],'persistentData':False,'purpose':'actual dormant renderer offline control','timeoutSeconds':15}
    RESOURCES.append(row)
    handle=None
    try:
        if os.name=='nt':
            from ctypes import wintypes
            kernel=ctypes.windll.kernel32
            kernel.GetProcessTimes.argtypes=[wintypes.HANDLE,*([ctypes.POINTER(wintypes.FILETIME)]*4)]
            kernel.GetProcessTimes.restype=wintypes.BOOL
            stamps=[wintypes.FILETIME() for _ in range(4)]
            handle=int(process._handle)
            if not kernel.GetProcessTimes(handle,*(ctypes.byref(s) for s in stamps)):raise RuntimeError('Owned birth query failed')
            birth=(stamps[0].dwHighDateTime<<32)|stamps[0].dwLowDateTime
            row.update(startFileTime=birth,actualStartUtc=(datetime(1601,1,1,tzinfo=timezone.utc)+timedelta(microseconds=birth//10)).isoformat())
        out,err=process.communicate(timeout=15)
        if len(out)>65536 or len(err)>65536:raise RuntimeError('Offline renderer output bound exceeded')
        return process.returncode,out,err
    finally:
        if process.poll() is None:
            process.terminate()
            try:process.wait(timeout=5)
            except subprocess.TimeoutExpired:process.kill();process.wait(timeout=5)
        row['verifiedExited']=process.returncode is not None
        process.stdout.close();process.stderr.close()
        if handle is not None:
            kernel.CloseHandle.argtypes=[ctypes.c_void_p];kernel.CloseHandle.restype=ctypes.c_int
            if not kernel.CloseHandle(handle):raise RuntimeError('Owned process handle close failed')
            detached=process._handle.Detach()
            if detached!=handle:raise RuntimeError('Owned wrapper detach mismatch')
            row['handleClosed']=True

class DormantDeploymentTests(unittest.TestCase):
    def reject(self,**arguments):
        code,out,_=run_renderer(**arguments)
        self.assertNotEqual(0,code);self.assertEqual(b'',out)
    def test_actual_render_preserves_disabled_single_replica_policy(self):
        code,out,err=run_renderer();self.assertEqual(0,code);self.assertEqual(b'',err)
        obj=json.loads(out)
        self.assertEqual('maliev-legacy',obj['metadata']['namespace'])
        self.assertEqual('false',obj['metadata']['annotations']['legacy.maliev.com/deployment-enabled'])
        self.assertEqual('1',obj['metadata']['annotations']['legacy.maliev.com/source-single-replica-target'])
        self.assertEqual(0,obj['spec']['replicas'])
        self.assertEqual({'type':'RollingUpdate','rollingUpdate':{'maxSurge':1,'maxUnavailable':0}},obj['spec']['strategy'])
        pod=obj['spec']['template']['spec'];container=pod['containers'][0]
        self.assertEqual('existing-offline-account',pod['serviceAccountName']);self.assertFalse(pod['automountServiceAccountToken'])
        self.assertEqual('existing-offline-secret',container['envFrom'][0]['secretRef']['name']);self.assertFalse(container['envFrom'][0]['secretRef']['optional'])
        self.assertEqual(IMAGE,container['image'])
        self.assertEqual('/'+OWNER+'/liveness',container['livenessProbe']['httpGet']['path'])
        self.assertEqual('/'+OWNER+'/readiness',container['readinessProbe']['httpGet']['path'])
        self.assertNotIn(b'__REQUIRED_',out)
    def test_repeat_render_does_not_mutate_source(self):
        before=TEMPLATE.read_bytes();first=run_renderer();second=run_renderer()
        self.assertEqual(0,first[0]);self.assertEqual(first,second);self.assertEqual(before,TEMPLATE.read_bytes())
    def test_mutable_foreign_and_invalid_digest_images(self):
        for image in [REPOSITORY+':latest',REPOSITORY+'@sha256:'+'0'*64,REPOSITORY+'@sha256:'+'A'*64,REPOSITORY+'@sha256:'+'1'*63,IMAGE+'\n',IMAGE.replace('legacy-maliev-'+OWNER,'legacy-maliev-foreign')]:
            with self.subTest(image=image):self.reject(image=image)
    def test_repository_approval_requires_exact_owner_and_repository(self):
        for repository in [REPOSITORY+'\n',REPOSITORY.replace('/offline-fixture/','/other/'),REPOSITORY.replace('/maliev-website/','/foreign/'),REPOSITORY.replace('legacy-maliev-'+OWNER,'legacy-maliev-foreign')]:
            with self.subTest(repository=repository):self.reject(repository=repository)
    def test_reference_injection_and_unapproved_namespace_are_rejected(self):
        for reference in ['Name','invalid/name','name\n','x'*64,'$(whoami)']:
            for key in ['account','secret']:
                with self.subTest(key=key,reference=reference):self.reject(**{key:reference})
    def test_modified_template_is_rejected_in_disposable_fixture(self):
        with tempfile.TemporaryDirectory(prefix='commerce-dormant-render-') as tmp:
            folder=Path(tmp).resolve();self.assertEqual(Path(tempfile.gettempdir()).resolve(),folder.parent)
            (folder/'scripts').mkdir();(folder/'deploy/disabled').mkdir(parents=True)
            script=folder/'scripts'/SCRIPT.name;script.write_bytes(SCRIPT.read_bytes())
            obj=json.loads(TEMPLATE.read_bytes());obj['spec']['replicas']=1
            (folder/'deploy/disabled'/TEMPLATE.name).write_text(json.dumps(obj),encoding='utf-8')
            self.reject(script=script)
        self.assertFalse(folder.exists())

if __name__=='__main__':
    result=unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(DormantDeploymentTests))
    receipt={'owner':OWNER,'testsRun':result.testsRun,'passed':result.wasSuccessful(),'controlsRun':CONTROL_COUNT,'resources':RESOURCES,'SDKStarted':False,'DockerStarted':False,'imageProvenance':False,'identityAcceptance':False,'deploymentAllowed':False}
    print(json.dumps(receipt),flush=True)
    raise SystemExit(0 if result.wasSuccessful() else 1)
