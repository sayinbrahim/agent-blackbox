// Runs inside a real VS Code (code --extensionTestsPath). See vscode/README.md for the command.
// BBX_PATH: bbx binary, BBX_TEST_FILE: a recorded file with AI-written lines.
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as vscode from "vscode";

export async function run(): Promise<void> {
  const file = process.env.BBX_TEST_FILE;
  assert.ok(file, "set BBX_TEST_FILE");
  await vscode.workspace.getConfiguration("agentBlackbox").update("bbxPath", process.env.BBX_PATH ?? "bbx", vscode.ConfigurationTarget.Global);

  const ext = vscode.extensions.all.find((e) => e.packageJSON.name === "agent-blackbox");
  assert.ok(ext, "extension not loaded");
  const api = (await ext.activate()) as { markedLines(uri: vscode.Uri): number[] | undefined };

  const uri = vscode.Uri.file(file);
  await vscode.window.showTextDocument(uri);
  let lines: number[] | undefined;
  for (let i = 0; i < 100 && !lines; i++) {
    await new Promise((r) => setTimeout(r, 200));
    lines = api.markedLines(uri);
  }
  assert.ok(lines && lines.length > 0, "no AI lines were marked");
  const summary = `marked ${lines.length} AI lines in ${file}: ${lines.join(",")}`;
  console.log(`agent-blackbox integration: ${summary}`);

  // Editing hides the markers until the file is saved.
  const editor = vscode.window.activeTextEditor!;
  await editor.edit((b) => b.insert(new vscode.Position(0, 0), "// edit\n"));
  assert.ok(editor.document.isDirty);
  await vscode.commands.executeCommand("workbench.action.revertFile");
  if (process.env.BBX_TEST_OUT) {
    fs.writeFileSync(process.env.BBX_TEST_OUT, `ok ${summary}
`);
  }
}
