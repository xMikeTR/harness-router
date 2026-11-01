"""Local Laya service: POST /classify {"text": ...} -> {"phase", "confidence"}. Binds to localhost only."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from laya import Router

MAX_CHARS = 1500  # roughly Laya's 512-token budget, leaving room for the questions
QUESTIONS = {"phase": {"type": "choice", "instructions": "What kind of software work is this request mainly asking for?", "criteria": {
    "plan": "decide an approach or design; no code yet",
    "implement": "add or change functionality",
    "debug": "find or fix a bug or failure",
    "test": "write or improve tests",
    "refactor": "restructure code without changing behaviour",
    "docs": "write documentation"}}}
router = Router()


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/classify":
            return self.send_error(404)
        body = json.loads(self.rfile.read(int(self.headers.get("content-length", 0))))
        answer = router.predict(body["text"][:MAX_CHARS], QUESTIONS)["answers"]["phase"]
        out = json.dumps({"phase": answer["choice"], "confidence": answer["answer_confidence"]}).encode()
        self.send_response(200)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    router.predict("warm up", QUESTIONS)
    print("laya ready on 127.0.0.1:8099", flush=True)
    ThreadingHTTPServer(("127.0.0.1", 8099), Handler).serve_forever()
