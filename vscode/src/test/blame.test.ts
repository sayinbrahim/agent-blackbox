import * as assert from "node:assert/strict";
import { test } from "node:test";
import { escapeMarkdown, hoverMarkdown, parseBlame, statusText } from "../blame";

const sample = JSON.stringify({
  file: "src/Order.cs", ai_lines: 2, human_lines: 0, ai_percent: 100, note: null,
  lines: [
    { line: 3, session: "c30ed484-a50f", session_start: "2026-11-23T14:38:42Z", prompt: 1, prompt_text: "Add <retry> & *backoff*", tool: "Edit", ts: "2026-11-23T10:11:00.000Z" },
    { line: 5, session: "c30ed484-a50f", prompt: null, prompt_text: null, tool: "Write", ts: null },
  ],
});

test("parses bbx blame --json", () => {
  const b = parseBlame(sample);
  assert.equal(b.file, "src/Order.cs");
  assert.deepEqual(b.lines.map((l) => l.line), [3, 5]);
  assert.equal(statusText(b), "AI 100%");
});

test("rejects output that is not blame JSON", () => {
  assert.throws(() => parseBlame("{}"));
  assert.throws(() => parseBlame("Line 3: AI-written"));
});

test("hover names the session, prompt and tool, and escapes the prompt", () => {
  const [l3, l5] = parseBlame(sample).lines;
  const h = hoverMarkdown(l3);
  assert.ok(h.includes("Session `c30ed484`, prompt #1: Add \\<retry\\> & \\*backoff\\*"), h);
  assert.ok(h.includes("Edit at 2026-11-23 10:11:00 UTC, unchanged since"), h);
  assert.ok(hoverMarkdown(l5).includes("no prompt recorded"));
  assert.ok(hoverMarkdown(l5).includes("Write at unknown time"));
});

test("escapeMarkdown escapes link and code syntax", () => {
  assert.equal(escapeMarkdown("[x](http://e) `y`"), "\\[x\\]\\(http://e\\) \\`y\\`");
});
