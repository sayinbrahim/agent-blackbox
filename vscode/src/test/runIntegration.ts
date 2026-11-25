// Downloads VS Code and runs src/test/integration.ts inside it, against a workspace with .blackbox/ recordings.
// Usage: node out/test/runIntegration.js <workspace> <file with AI lines> <bbx path>
import * as path from "node:path";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
  const [workspace, file, bbx] = process.argv.slice(2);
  if (!workspace || !file || !bbx) {
    throw new Error("usage: runIntegration <workspace> <file> <bbx>");
  }
  await runTests({
    extensionDevelopmentPath: path.resolve(__dirname, "../.."),
    extensionTestsPath: path.resolve(__dirname, "integration"),
    launchArgs: [path.resolve(workspace), "--disable-extensions", "--disable-workspace-trust"],
    extensionTestsEnv: { BBX_PATH: path.resolve(bbx), BBX_TEST_FILE: path.resolve(file) },
  });
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
