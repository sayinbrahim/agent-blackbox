// Pure helpers with no vscode import, so they run under node --test.
import { execFile } from "node:child_process";

export interface AiLine {
  line: number;
  session: string;
  session_start?: string;
  prompt: number | null;
  prompt_text: string | null;
  tool: string | null;
  ts: string | null;
}

export interface BlameJson {
  file: string;
  ai_lines: number;
  human_lines: number;
  ai_percent: number;
  note: string | null;
  lines: AiLine[];
}

export function parseBlame(stdout: string): BlameJson {
  const json = JSON.parse(stdout) as BlameJson;
  if (typeof json.file !== "string" || !Array.isArray(json.lines)) {
    throw new Error("unexpected output from bbx blame --json");
  }
  return json;
}

/** Hover text for one AI line, as Markdown. Prompt text is escaped: it is user content. */
export function hoverMarkdown(l: AiLine): string {
  const when = l.ts ? l.ts.replace("T", " ").replace(/\.\d+Z$/, " UTC") : "unknown time";
  const prompt = l.prompt !== null
    ? `prompt #${l.prompt}${l.prompt_text ? `: ${escapeMarkdown(l.prompt_text)}` : ""}`
    : "no prompt recorded";
  return [
    "**AI-written** (agent-blackbox)",
    "",
    `Session \`${l.session.slice(0, 8)}\`, ${prompt}`,
    "",
    `${l.tool ?? "?"} at ${when}, unchanged since`,
  ].join("\n");
}

export function statusText(b: BlameJson): string {
  return `AI ${b.ai_percent}%`;
}

export function escapeMarkdown(s: string): string {
  return s.replace(/[\\`*_{}[\]()#+\-.!|<>]/g, (c) => "\\" + c);
}

export type RunResult =
  | { kind: "ok"; blame: BlameJson }
  | { kind: "none"; message: string } // no .blackbox/, file outside the project, ...
  | { kind: "missing" } // bbx not found
  | { kind: "old" }; // bbx without --json

/** Runs bbx blame --json in the file's directory; bbx finds the project root from there. */
export function runBlame(bbx: string, cwd: string, file: string, timeoutMs = 15000): Promise<RunResult> {
  return new Promise((resolve) => {
    execFile(bbx, ["blame", file, "--json"], { cwd, timeout: timeoutMs, windowsHide: true, maxBuffer: 16 * 1024 * 1024 },
      (error, stdout, stderr) => {
        if (error && (error as NodeJS.ErrnoException).code === "ENOENT") {
          resolve({ kind: "missing" });
        } else if (error) {
          resolve(/--json|Unrecognized/i.test(stderr) ? { kind: "old" } : { kind: "none", message: stderr.trim() || error.message });
        } else {
          try {
            resolve({ kind: "ok", blame: parseBlame(stdout) });
          } catch (e) {
            resolve({ kind: "none", message: String(e) });
          }
        }
      });
  });
}
