import * as path from "node:path";
import * as vscode from "vscode";
import { hoverMarkdown, runBlame, statusText } from "./blame";

let decoration: vscode.TextEditorDecorationType;
let status: vscode.StatusBarItem;
let warned = false;
const generation = new Map<string, number>(); // drops results that arrive after a newer request
const shown = new Map<string, number[]>(); // marked line numbers per document, for the integration test

export function activate(context: vscode.ExtensionContext): { markedLines(uri: vscode.Uri): number[] | undefined } {
  decoration = vscode.window.createTextEditorDecorationType({
    gutterIconPath: context.asAbsolutePath("media/ai.svg"),
    gutterIconSize: "contain",
    overviewRulerColor: "rgba(229, 165, 10, 0.7)",
    overviewRulerLane: vscode.OverviewRulerLane.Left,
  });
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  status.tooltip = "Share of non-trivial lines written by the AI agent, from agent-blackbox recordings";
  context.subscriptions.push(
    decoration,
    status,
    vscode.commands.registerCommand("agentBlackbox.toggle", async () => {
      const config = vscode.workspace.getConfiguration("agentBlackbox");
      await config.update("enabled", !config.get<boolean>("enabled", true), vscode.ConfigurationTarget.Global);
    }),
    vscode.commands.registerCommand("agentBlackbox.refresh", () => refreshAll()),
    vscode.window.onDidChangeActiveTextEditor((e) => e && refresh(e)),
    vscode.window.onDidChangeVisibleTextEditors(() => refreshAll()),
    vscode.workspace.onDidSaveTextDocument(() => refreshAll()),
    vscode.workspace.onDidChangeTextDocument((e) => {
      // Lines shift while editing; markers come back on save.
      if (!e.document.isDirty) {
        return;
      }
      for (const editor of vscode.window.visibleTextEditors.filter((v) => v.document === e.document)) {
        editor.setDecorations(decoration, []);
        if (editor === vscode.window.activeTextEditor) {
          status.text = "AI: save to update";
        }
      }
    }),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("agentBlackbox")) {
        refreshAll();
      }
    }),
  );
  refreshAll();
  return { markedLines: (uri) => shown.get(uri.toString()) };
}

export function deactivate(): void {}

function refreshAll(): void {
  for (const editor of vscode.window.visibleTextEditors) {
    void refresh(editor);
  }
  if (!vscode.window.activeTextEditor) {
    status.hide();
  }
}

async function refresh(editor: vscode.TextEditor): Promise<void> {
  const doc = editor.document;
  const config = vscode.workspace.getConfiguration("agentBlackbox");
  const active = () => editor === vscode.window.activeTextEditor;
  if (!config.get<boolean>("enabled", true) || doc.uri.scheme !== "file" || doc.isDirty) {
    editor.setDecorations(decoration, []);
    if (active()) {
      status.hide();
    }
    return;
  }
  const key = doc.uri.toString();
  const gen = (generation.get(key) ?? 0) + 1;
  generation.set(key, gen);

  const result = await runBlame(config.get<string>("bbxPath", "bbx"), path.dirname(doc.uri.fsPath), doc.uri.fsPath);
  if (generation.get(key) !== gen || doc.isDirty) {
    return;
  }
  if (result.kind !== "ok") {
    editor.setDecorations(decoration, []);
    if (active()) {
      status.hide();
    }
    if ((result.kind === "missing" || result.kind === "old") && !warned) {
      warned = true;
      void vscode.window.showWarningMessage(result.kind === "missing"
        ? "agent-blackbox: bbx was not found. Install it with 'dotnet tool install -g AgentBlackbox' or set agentBlackbox.bbxPath."
        : "agent-blackbox: this bbx is too old for the extension. Update it to 0.3.0 or later.");
    }
    return;
  }
  const ranges = result.blame.lines
    .filter((l) => l.line >= 1 && l.line <= doc.lineCount)
    .map((l) => ({ range: doc.lineAt(l.line - 1).range, hoverMessage: new vscode.MarkdownString(hoverMarkdown(l)) }));
  editor.setDecorations(decoration, ranges);
  shown.set(key, result.blame.lines.map((l) => l.line));
  if (active()) {
    status.text = statusText(result.blame);
    status.show();
  }
}
