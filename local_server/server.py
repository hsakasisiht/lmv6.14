#!/usr/bin/env python3
"""
LordsBot v6 Licensing & Settings Mock Server
Serves all authentication, session management, heartbeat, and game version endpoints
matching the exact contract defined in IconHook.cs and updatediconhook.cs.

Endpoints served:
  - POST /api/lordsbotv1/activate   (Initial license activation)
  - POST /api/lordsbotv1/validate   (Session re-validation)
  - POST /api/lordsbotv1/heartbeat  (Periodic keep-alive heartbeat)
  - GET  /api/settings/gameversion  (Game & bot version tracker)
  - GET  /api/lordsbotv1/sessions   (Admin: inspect active sessions)
  - GET  /health                    (Health check)
"""

import sys
import os
import json
import time
import uuid
import secrets
import argparse
import datetime
import threading
from http.server import HTTPServer, BaseHTTPRequestHandler
from socketserver import ThreadingMixIn

# Server configuration defaults
DEFAULT_HOST = "0.0.0.0"
DEFAULT_PORT = 5000
DEFAULT_SLOTS = 1000
DEFAULT_GAME_VERSION = "v6.13"
SESSIONS_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sessions.json")

# In-memory session store (protected by thread lock)
sessions_lock = threading.Lock()
sessions = {}


sessions_dirty = False
sessions_flusher_started = False

def mark_sessions_dirty():
    global sessions_dirty
    sessions_dirty = True

def flush_sessions_worker():
    global sessions_dirty
    while True:
        time.sleep(15)
        if sessions_dirty:
            try:
                with sessions_lock:
                    to_save = dict(sessions)
                    sessions_dirty = False
                temp_file = SESSIONS_FILE + ".tmp"
                with open(temp_file, "w", encoding="utf-8") as f:
                    json.dump(to_save, f, separators=(',', ':'))
                os.replace(temp_file, SESSIONS_FILE)
            except Exception as e:
                print(f"[!] Warning: Could not flush {SESSIONS_FILE}: {e}")

def save_sessions(immediate=False):
    global sessions_dirty
    if immediate:
        try:
            with sessions_lock:
                to_save = dict(sessions)
                sessions_dirty = False
            temp_file = SESSIONS_FILE + ".tmp"
            with open(temp_file, "w", encoding="utf-8") as f:
                json.dump(to_save, f, separators=(',', ':'))
            os.replace(temp_file, SESSIONS_FILE)
        except Exception as e:
            print(f"[!] Warning: Could not save {SESSIONS_FILE}: {e}")
    else:
        mark_sessions_dirty()


def load_sessions():
    global sessions, sessions_flusher_started
    if os.path.exists(SESSIONS_FILE):
        try:
            with open(SESSIONS_FILE, "r", encoding="utf-8") as f:
                sessions = json.load(f)
            print(f"[*] Loaded {len(sessions)} session(s) from {SESSIONS_FILE}")
        except Exception as e:
            print(f"[!] Warning: Could not load {SESSIONS_FILE}: {e}")

    if not sessions_flusher_started:
        sessions_flusher_started = True
        t = threading.Thread(target=flush_sessions_worker, daemon=True)
        t.start()


def generate_challenge() -> str:
    """Generate a non-empty, cryptographically secure challenge nonce."""
    return secrets.token_hex(16)


def format_iso_timestamp(dt: datetime.datetime) -> str:
    """Format UTC datetime as ISO-8601 string expected by DateTimeOffset.Parse."""
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


import subprocess

signer_process = None
signer_lock = threading.Lock()

def get_signer_process():
    global signer_process
    with signer_lock:
        if signer_process is not None and signer_process.poll() is None:
            return signer_process

        base_dir = os.path.dirname(os.path.abspath(__file__))
        parent_dir = os.path.dirname(base_dir)
        candidates = [
            os.path.join(parent_dir, "lib", "local-signer", "LocalSignerHost.exe"),
            os.path.join(base_dir, "lib", "local-signer", "LocalSignerHost.exe"),
            r"C:\LordsBot_Dist\Bot\lib\local-signer\LocalSignerHost.exe",
            os.path.join(base_dir, "LordsBotGate", "lib", "local-signer", "LocalSignerHost.exe"),
            r"E:\mylb\lib\local-signer\LocalSignerHost.exe",
            r"E:\mylb\LordsBotGate\lib\local-signer\LocalSignerHost.exe",
            r"E:\mylb\LordsBot_Updated\lib\local-signer\LocalSignerHost.exe"
        ]
        exe_path = None
        for c in candidates:
            if os.path.exists(c):
                exe_path = os.path.abspath(c)
                break

        if not exe_path:
            print("[SIGNER] Error: LocalSignerHost.exe not found on server!")
            return None

        try:
            working_dir = os.path.dirname(exe_path)
            signer_process = subprocess.Popen(
                [exe_path, "--server"],
                cwd=working_dir,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1
            )
            print(f"[SIGNER] Started backend LocalSignerHost (PID {signer_process.pid}) from {exe_path}")
            return signer_process
        except Exception as e:
            print(f"[SIGNER] Failed starting LocalSignerHost: {e}")
            return None

def handle_sign_request(req_data: dict) -> dict:
    proc = get_signer_process()
    if not proc:
        return {"Error": "Signer host not available on server", "Headers": {}}

    req_obj = {
        "ResourcePath": req_data.get("ResourcePath") or req_data.get("resourcePath") or "",
        "RequestBody": req_data.get("RequestBody") or req_data.get("requestBody") or "",
        "Uuid": req_data.get("Uuid") or req_data.get("uuid") or "",
        "Udid": req_data.get("Udid") or req_data.get("udid") or "",
        "GameId": str(req_data.get("GameId") or req_data.get("gameId") or "1051029902"),
        "ExtraParams": req_data.get("ExtraParams") or req_data.get("extraParams") or {}
    }

    with signer_lock:
        try:
            line = json.dumps(req_obj) + "\n"
            proc.stdin.write(line)
            proc.stdin.flush()

            resp_line = proc.stdout.readline()
            if not resp_line:
                print("[SIGNER] Backend signer returned empty line")
                return {"Error": "Empty response from signer", "Headers": {}}

            data = json.loads(resp_line)
            return {
                "success": data.get("Error") is None,
                "Headers": data.get("Headers", {}) or {},
                "Error": data.get("Error")
            }
        except Exception as e:
            print(f"[SIGNER] Exception during sign: {e}")
            return {"Error": str(e), "Headers": {}}

class LordsBotRequestHandler(BaseHTTPRequestHandler):
    server_version = "LordsBotLicenseServer/6.13"
    sys_version = ""

    def log_message(self, format, *args):
        # Suppress standard http.server noise, custom logging handled per endpoint
        pass

    def _send_json(self, status_code: int, data: dict):
        response_bytes = json.dumps(data, indent=2).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(response_bytes)))
        self.send_header("Connection", "keep-alive")
        self.end_headers()
        self.wfile.write(response_bytes)

    def _read_json_body(self) -> dict:
        content_length = int(self.headers.get("Content-Length", 0))
        if content_length <= 0:
            return {}
        raw_body = self.rfile.read(content_length).decode("utf-8")
        try:
            return json.loads(raw_body)
        except json.JSONDecodeError:
            return {}

    def do_GET(self):
        normalized_path = self.path.split("?")[0].rstrip("/").lower()

        # Endpoint: GET /api/settings/gameversion
        if normalized_path == "/api/settings/gameversion":
            self.handle_game_version()
            return

        # Endpoint: GET /api/auth/login
        if normalized_path == "/api/auth/login":
            self._send_json(200, {
                "success": True,
                "status": "online",
                "token": "sess_local_gate_master",
                "sessionId": "sess_local_gate_master",
                "slots": self.server.slots,
                "allowedSlots": self.server.slots,
                "game_version": self.server.game_version,
                "version": self.server.game_version
            })
            return

        # Endpoint: GET /api/lordsbotv1/sessions
        if normalized_path == "/api/lordsbotv1/sessions":
            with sessions_lock:
                self._send_json(200, {
                    "success": True,
                    "count": len(sessions),
                    "sessions": list(sessions.values())
                })
            return

        # Endpoint: GET /health or /
        if normalized_path in ("", "/health"):
            with sessions_lock:
                active_count = len(sessions)
            self._send_json(200, {
                "status": "online",
                "service": "LordsBot v6 Licensing & Settings Server",
                "active_sessions": active_count,
                "version": self.server.game_version,
                "timestamp": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc))
            })
            return

        # Fallback 404
        self._send_json(404, {"error": "Not Found", "path": self.path})

    def do_POST(self):
        normalized_path = self.path.split("?")[0].rstrip("/").lower()

        if normalized_path in ("/api/signer/sign", "/api/sign"):
            req_data = self._read_json_body()
            resp = handle_sign_request(req_data)
            hdr_count = len(resp.get("Headers", {}))
            print(f"[SIGNER API] Signed request for GameId={req_data.get('GameId')}, Resource={req_data.get('ResourcePath')}, returned {hdr_count} headers (err={resp.get('Error')}).")
            self._send_json(200, resp)
            return

        if normalized_path == "/api/lordsbotv1/activate":
            self.handle_activate()
        elif normalized_path == "/api/lordsbotv1/validate":
            self.handle_validate()
        elif normalized_path == "/api/lordsbotv1/heartbeat":
            self.handle_heartbeat()
        else:
            self._send_json(404, {"error": "Not Found", "path": self.path})

    def handle_activate(self):
        """
        POST /api/lordsbotv1/activate
        Payload: { "license": "...", "deviceId": "...", "version": "6.10" }
        Required response keys per IconHook.cs:
          - "V6": true
          - "success": true
          - "valid": true
          - "status": "active"
          - "challenge": "<non-empty string>"
          - "sessionId": "<string>"
          - "slots": <int>
          - "expiresAt": "<ISO-8601 string>" or null
        """
        body = self._read_json_body()
        license_key = body.get("license", "").strip()
        device_id = body.get("deviceId", "unknown")
        client_version = body.get("version", "6.13")

        if not license_key:
            license_key = "test"
            print(f"[*] [ACTIVATE AUTO-ASSIGN] Empty license key from device '{device_id}' -> auto-activated default '{license_key}'")

        session_id = str(uuid.uuid4())
        challenge = generate_challenge()
        expires_dt = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=self.server.expires_days)
        expires_str = format_iso_timestamp(expires_dt)

        session_record = {
            "license": license_key,
            "deviceId": device_id,
            "sessionId": session_id,
            "challenge": challenge,
            "slots": self.server.slots,
            "expiresAt": expires_str,
            "created": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc)),
            "lastSeen": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc)),
            "clientVersion": client_version
        }

        with sessions_lock:
            sessions[session_id] = session_record
            save_sessions(immediate=True)

        print(f"[+] [ACTIVATE] License='{license_key}' | Device='{device_id}' | Slots={self.server.slots} | SessionId='{session_id}'")

        self._send_json(200, {
            "V6": True,
            "success": True,
            "valid": True,
            "status": "active",
            "sessionId": session_id,
            "challenge": challenge,
            "slots": self.server.slots,
            "expiresAt": expires_str
        })

    def handle_validate(self):
        """
        POST /api/lordsbotv1/validate
        Payload: { "license": "...", "deviceId": "...", "version": "6.10", "sessionId": "...", "challenge": "..." }
        """
        body = self._read_json_body()
        license_key = body.get("license", "").strip()
        device_id = body.get("deviceId", "unknown")
        session_id = body.get("sessionId", "")
        old_challenge = body.get("challenge", "")

        new_challenge = generate_challenge()
        expires_dt = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=self.server.expires_days)
        expires_str = format_iso_timestamp(expires_dt)

        with sessions_lock:
            if session_id in sessions:
                session_record = sessions[session_id]
                session_record["challenge"] = new_challenge
                session_record["lastSeen"] = format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc))
                slots = session_record.get("slots", self.server.slots)
            else:
                # If session not found, auto-register to keep local development frictionless
                session_id = session_id if session_id else str(uuid.uuid4())
                session_record = {
                    "license": license_key,
                    "deviceId": device_id,
                    "sessionId": session_id,
                    "challenge": new_challenge,
                    "slots": self.server.slots,
                    "expiresAt": expires_str,
                    "created": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc)),
                    "lastSeen": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc))
                }
                sessions[session_id] = session_record
                slots = self.server.slots

            save_sessions(immediate=False)

        print(f"[+] [VALIDATE] License='{license_key}' | SessionId='{session_id}' | Rotated Challenge='{new_challenge[:8]}...'")

        self._send_json(200, {
            "V6": True,
            "success": True,
            "valid": True,
            "status": "active",
            "sessionId": session_id,
            "challenge": new_challenge,
            "slots": slots,
            "expiresAt": expires_str
        })

    def handle_heartbeat(self):
        """
        POST /api/lordsbotv1/heartbeat
        Payload: { "license": "...", "deviceId": "...", "version": "6.10", "sessionId": "...", "challenge": "..." }
        """
        body = self._read_json_body()
        license_key = body.get("license", "").strip()
        device_id = body.get("deviceId", "unknown")
        session_id = body.get("sessionId", "")

        new_challenge = generate_challenge()
        expires_dt = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=self.server.expires_days)
        expires_str = format_iso_timestamp(expires_dt)

        with sessions_lock:
            if session_id in sessions:
                session_record = sessions[session_id]
                session_record["challenge"] = new_challenge
                session_record["lastSeen"] = format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc))
                slots = session_record.get("slots", self.server.slots)
            else:
                session_id = session_id if session_id else str(uuid.uuid4())
                session_record = {
                    "license": license_key,
                    "deviceId": device_id,
                    "sessionId": session_id,
                    "challenge": new_challenge,
                    "slots": self.server.slots,
                    "expiresAt": expires_str,
                    "created": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc)),
                    "lastSeen": format_iso_timestamp(datetime.datetime.now(datetime.timezone.utc))
                }
                sessions[session_id] = session_record
                slots = self.server.slots
                save_sessions(immediate=False)

        print(f"[*] [HEARTBEAT] SessionId='{session_id}' | Time={session_record['lastSeen']}")

        self._send_json(200, {
            "V6": True,
            "success": True,
            "valid": True,
            "status": "active",
            "sessionId": session_id,
            "challenge": new_challenge,
            "slots": slots,
            "expiresAt": expires_str
        })

    def handle_game_version(self):
        """
        GET /api/settings/gameversion
        IconHook expects:
          doc.RootElement.GetProperty("gameversion").GetProperty("lordsbot").GetString()
        """
        gv = read_game_version_from_file()
        print(f"[*] [GAMEVERSION] Serving game='{gv}', lordsbot='{self.server.game_version}'")
        self._send_json(200, {
            "gameversion": {
                "lordsbot": self.server.game_version,
                "game": gv,
                "game_version": gv,
                "version": self.server.game_version
            },
            "game_version": gv,
            "game": gv,
            "success": True
        })


def read_game_version_from_file():
    candidates = [
        "game_version.txt",
        "../game_version.txt",
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "game_version.txt"),
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "game_version.txt")
    ]
    for p in candidates:
        try:
            if os.path.isfile(p):
                with open(p, "r", encoding="utf-8") as f:
                    v = f.read().strip()
                    if v and "." in v:
                        return v
        except Exception:
            pass
    return "2.208.320"


class ThreadedLordsServer(ThreadingMixIn, HTTPServer):
    daemon_threads = True
    request_queue_size = 256

    def __init__(self, server_address, RequestHandlerClass, slots=DEFAULT_SLOTS, game_version=DEFAULT_GAME_VERSION, expires_days=3650):
        super().__init__(server_address, RequestHandlerClass)
        self.slots = slots
        self.game_version = game_version
        self.expires_days = expires_days


def generate_self_signed_cert(cert_path="cert.pem", key_path="key.pem"):
    """Generate self-signed certificate using OpenSSL if requested."""
    import subprocess
    cmd = [
        "openssl", "req", "-x509", "-newkey", "rsa:2048",
        "-keyout", key_path, "-out", cert_path, "-days", "3650",
        "-nodes", "-subj", "/CN=api2.lordscrack.net"
    ]
    try:
        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print(f"[+] Generated self-signed SSL certificate: {cert_path}, {key_path}")
        return True
    except Exception as e:
        print(f"[!] Warning: OpenSSL certificate generation failed: {e}")
        return False


def main():
    parser = argparse.ArgumentParser(description="LordsBot v6 Licensing & Settings Server")
    parser.add_argument("--host", default=DEFAULT_HOST, help=f"Host interface to bind (default: {DEFAULT_HOST})")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"Port to listen on (default: {DEFAULT_PORT})")
    parser.add_argument("--slots", type=int, default=DEFAULT_SLOTS, help=f"Maximum account slots (default: {DEFAULT_SLOTS})")
    parser.add_argument("--version", default=DEFAULT_GAME_VERSION, help=f"Bot version string (default: {DEFAULT_GAME_VERSION})")
    parser.add_argument("--days", type=int, default=3650, help="License duration in days (default: 3650 = 10 years)")
    parser.add_argument("--ssl", action="store_true", help="Enable HTTPS mode (TLS)")
    parser.add_argument("--cert", default="cert.pem", help="SSL certificate file path")
    parser.add_argument("--key", default="key.pem", help="SSL private key file path")
    args = parser.parse_args()

    load_sessions()

    server = ThreadedLordsServer(
        (args.host, args.port),
        LordsBotRequestHandler,
        slots=args.slots,
        game_version=args.version,
        expires_days=args.days
    )

    if args.ssl or args.port == 443:
        import ssl
        if not (os.path.exists(args.cert) and os.path.exists(args.key)):
            generate_self_signed_cert(args.cert, args.key)
        if os.path.exists(args.cert) and os.path.exists(args.key):
            ctx = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
            ctx.load_cert_chain(certfile=args.cert, keyfile=args.key)
            server.socket = ctx.wrap_socket(server.socket, server_side=True)
            proto = "https"
        else:
            proto = "http"
    else:
        proto = "http"

    print("=" * 65)
    print(f"  LordsBot v6 Licensing & Settings Mock Server")
    print(f"  Listening on: {proto}://{args.host}:{args.port}")
    print(f"  Account Slots: {args.slots}")
    print(f"  Game Version: {args.version}")
    print(f"  Active Endpoints:")
    print(f"    - POST {proto}://{args.host}:{args.port}/api/lordsbotv1/activate")
    print(f"    - POST {proto}://{args.host}:{args.port}/api/lordsbotv1/validate")
    print(f"    - POST {proto}://{args.host}:{args.port}/api/lordsbotv1/heartbeat")
    print(f"    - GET  {proto}://{args.host}:{args.port}/api/settings/gameversion")
    print(f"    - GET  {proto}://{args.host}:{args.port}/api/lordsbotv1/sessions")
    print(f"    - GET  {proto}://{args.host}:{args.port}/health")
    print("=" * 65)
    print("Server is ready. Press Ctrl+C to terminate.")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[*] Shutting down server...")
        server.shutdown()
        server.server_close()
        print("[*] Server stopped cleanly.")


if __name__ == "__main__":
    main()
