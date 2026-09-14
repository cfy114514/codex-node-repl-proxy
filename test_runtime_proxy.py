"""Run with Python on Windows; uses the installed .NET Framework compiler only."""
import base64
import ctypes
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import queue
import threading

ROOT = Path(__file__).resolve().parent
CSC = Path(os.environ['SystemRoot']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'

with tempfile.TemporaryDirectory(prefix='codex-proxy-test-') as directory:
    tmp = Path(directory)
    wrapper = tmp / 'proxy.exe'
    subprocess.run([str(CSC), '/nologo', '/platform:x64', '/out:' + str(wrapper), str(ROOT / 'RuntimeProxy.cs')], check=True)
    (tmp / 'node.exe').touch()
    fixture = tmp / 'fixture.cs'
    fixture.write_text('''using System; using System.Diagnostics; using System.Text;
class Fixture { static int Main(string[] args) {
 Console.WriteLine(Process.GetCurrentProcess().Id);
 foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY","NODE_USE_ENV_PROXY","BROWSER_USE_DISABLE_AMBIENT_NETWORK"})
  Console.WriteLine(key+"="+Environment.GetEnvironmentVariable(key));
 foreach(string arg in args) Console.WriteLine("ARG="+Convert.ToBase64String(Encoding.UTF8.GetBytes(arg)));
 Console.Error.WriteLine("fixture stderr");
 string input=Console.ReadLine();
 while(input=="PING") { Console.WriteLine("PONG"); input=Console.ReadLine(); }
 if(input=="WAIT") System.Threading.Thread.Sleep(30000);
 Console.WriteLine("INPUT="+input); return 7;
} }''', encoding='utf-8')
    subprocess.run([str(CSC), '/nologo', '/platform:x64', '/out:' + str(tmp / 'node_repl.exe'), str(fixture)], check=True)
    proxy_file = tmp / 'proxy-url.txt'
    proxy_file.write_text('http://127.0.0.1:7897\n', encoding='utf-8')
    env = dict(os.environ, NODE_REPL_NODE_PATH=str(tmp / 'node.exe'), HTTP_PROXY='http://wrong.invalid:1', NODE_USE_ENV_PROXY='0')
    args = ['', 'plain', 'with space', '中 文', 'a"b', 'a\\"b', 'C:\\path with space\\', '\\', '\\\\', 'x\\\\"y']
    result = subprocess.run([str(wrapper), *args], env=env, input='MCP stdin\n', text=True, capture_output=True, timeout=10)
    assert result.returncode == 7, result.stderr
    lines = result.stdout.splitlines()
    assert lines[0].isdigit(), result.stdout
    for key in ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY']:
        assert f'{key}=http://127.0.0.1:7897' in lines
    assert 'NO_PROXY=localhost,127.0.0.1,::1' in lines
    assert 'NODE_USE_ENV_PROXY=1' in lines
    assert 'BROWSER_USE_DISABLE_AMBIENT_NETWORK=0' in lines
    assert [base64.b64decode(line[4:]).decode() for line in lines if line.startswith('ARG=')] == args
    assert lines[-1] == 'INPUT=MCP stdin'
    assert result.stderr.strip() == 'fixture stderr'

    # Reject invalid proxy settings before launching the runtime.
    proxy_file.write_text('not a proxy', encoding='utf-8')
    rejected = subprocess.run([str(wrapper)], env=env, capture_output=True, text=True, timeout=5)
    assert rejected.returncode == 1 and not rejected.stdout and 'proxy-url.txt' in rejected.stderr
    proxy_file.write_text('http://127.0.0.1:7897', encoding='utf-8')
    missing = subprocess.run([str(wrapper)], env=dict(env, NODE_REPL_NODE_PATH=''), capture_output=True, text=True, timeout=5)
    assert missing.returncode == 1 and not missing.stdout and 'NODE_REPL_NODE_PATH' in missing.stderr

    # Force-killing the wrapper must terminate the child instead of orphaning it.
    proc = subprocess.Popen([str(wrapper)], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        child_pid = int(proc.stdout.readline())
        for _ in range(6): proc.stdout.readline()
        proc.stdin.write('PING\n'); proc.stdin.flush()
        reply = queue.Queue()
        threading.Thread(target=lambda: reply.put(proc.stdout.readline()), daemon=True).start()
        assert reply.get(timeout=3).strip() == 'PONG', 'Interactive MCP input was buffered'
        proc.stdin.write('WAIT\n'); proc.stdin.flush()
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x00100000, False, child_pid)
        assert handle, ctypes.get_last_error()
        try:
            proc.kill(); proc.wait(timeout=5)
            assert kernel.WaitForSingleObject(handle, 5000) == 0, 'Orphan runtime survived wrapper termination'
        finally: kernel.CloseHandle(handle)
    finally:
        if proc.poll() is None: proc.kill()
        proc.communicate(timeout=5)
    print('PASS: proxy inheritance, stdio, Unicode/argument quoting, exit code, validation, child cleanup')
