#!/usr/bin/env node
import { spawn } from "node:child_process";
import { copyFile, cp, mkdir, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { basename, dirname, extname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { normalizeLogicProfile } from "./cli-options.js";
import { validateDocument } from "./schema.js";

async function main(): Promise<void> {
  const [, , command, ...args] = process.argv;
  if (!command || command === "help" || command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  if (command === "validate") {
    const input = requireArgument(args[0], "input file");
    const document = JSON.parse(await readFile(resolve(input), "utf8")) as unknown;
    const result = validateDocument(document);
    if (!result.valid) {
      for (const error of result.errors) console.error(`error: ${error}`);
      process.exitCode = 1;
      return;
    }
    console.log(`${input}: valid three-unity-scene v${(document as { version: number }).version}`);
    return;
  }

  if (command === "pack") {
    const input = requireArgument(args[0], "input JSON file");
    const outputIndex = args.findIndex((value) => value === "-o" || value === "--output");
    const output = outputIndex >= 0 ? requireArgument(args[outputIndex + 1], "output file") : `${input.replace(/\.json$/i, "")}.threeunity`;
    const text = await readFile(resolve(input), "utf8");
    const document = JSON.parse(text) as unknown;
    const result = validateDocument(document);
    if (!result.valid) throw new Error(result.errors.join("\n"));
    await mkdir(dirname(resolve(output)), { recursive: true });
    await writeFile(resolve(output), `${JSON.stringify(document, null, 2)}\n`, "utf8");
    console.log(`Wrote ${resolve(output)}`);
    return;
  }

  if (command === "install-unity") {
    const project = resolve(requireArgument(args[0], "Unity project directory"));
    const projectAssets = join(project, "Assets");
    const info = await stat(projectAssets).catch(() => null);
    if (!info?.isDirectory()) throw new Error(`${project} is not a Unity project (Assets directory not found).`);
    const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "unity-package");
    const destination = join(project, "Packages", "com.three-unity.bridge");
    await mkdir(dirname(destination), { recursive: true });
    await rm(destination, { recursive: true, force: true });
    await cp(packageRoot, destination, { recursive: true, force: true });
    console.log(`Installed Unity package at ${destination}`);
    return;
  }

  if (command === "copy") {
    const input = resolve(requireArgument(args[0], "input .threeunity file"));
    const unityProject = resolve(requireArgument(args[1], "Unity project directory"));
    const destination = join(unityProject, "Assets", basename(input));
    await copyFile(input, destination);
    console.log(`Copied scene asset to ${destination}`);
    return;
  }

  if (command === "build-unity") {
    const input = resolve(requireArgument(args[0], "input .threeunity file"));
    const project = resolve(requireArgument(args[1], "Unity project directory"));
    await requireUnityProject(project);
    const document = JSON.parse(await readFile(input, "utf8")) as unknown;
    const validation = validateDocument(document);
    if (!validation.valid) throw new Error(validation.errors.join("\n"));

    const unityFlag = optionValue(args, "--unity");
    const outputFlag = optionValue(args, "--output") ?? optionValue(args, "-o");
    const unity = await findUnityExecutable(unityFlag);
    const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "unity-package");
    const packageDestination = join(project, "Packages", "com.three-unity.bridge");
    await mkdir(dirname(packageDestination), { recursive: true });
    await rm(packageDestination, { recursive: true, force: true });
    await cp(packageRoot, packageDestination, { recursive: true, force: true });

    const assetDirectory = join(project, "Assets", "ThreeUnityBridge");
    await mkdir(assetDirectory, { recursive: true });
    const assetName = basename(input);
    await copyFile(input, join(assetDirectory, assetName));
    const assetPath = `Assets/ThreeUnityBridge/${assetName}`;
    const stem = assetName.slice(0, -extname(assetName).length);
    const output = resolve(outputFlag ?? join(project, "Build", `${stem}.exe`));
    const log = join(project, "ThreeUnityBridge-build.log");

    await runUnity(unity, [
      "-batchmode", "-nographics", "-quit",
      "-projectPath", project,
      "-executeMethod", "ThreeUnity.Bridge.Editor.ThreeUnityBatchBuilder.BuildFromCommandLine",
      "-threeUnityAsset", assetPath,
      "-threeUnityScene", `Assets/ThreeUnityBridge/${stem}Playable.unity`,
      "-threeUnityOutput", output,
      "-logFile", log,
    ]);
    const built = await stat(output).catch(() => null);
    if (!built?.isFile()) throw new Error(`Unity exited without producing ${output}. See ${log}`);
    console.log(`Built Unity player at ${output} (${built.size} bytes)`);
    console.log(`Unity log: ${log}`);
    return;
  }

  if (command === "build-web-unity") {
    if (process.platform !== "win32") throw new Error("The WebView2 bridge currently supports Windows only.");
    const webDist = resolve(requireArgument(args[0], "web dist directory"));
    const project = resolve(requireArgument(args[1], "Unity project directory"));
    await requireUnityProject(project);
    const distInfo = await stat(webDist).catch(() => null);
    if (!distInfo?.isDirectory()) throw new Error(`Web dist directory not found: ${webDist}`);

    const requestedEntry = optionValue(args, "--entry") ?? "index.html";
    const entryPath = resolve(webDist, requestedEntry);
    const relativeEntry = relative(webDist, entryPath);
    if (!relativeEntry || relativeEntry.startsWith("..") || isAbsolute(relativeEntry))
      throw new Error(`Web entry must stay inside the dist directory: ${requestedEntry}`);
    if (!(await stat(entryPath).catch(() => null))?.isFile())
      throw new Error(`Web entry file not found: ${entryPath}`);
    const entry = relativeEntry.replaceAll("\\", "/");
    const productName = optionValue(args, "--name") ?? basename(dirname(webDist));
    const safeName = productName.replace(/[<>:\"/\\|?*]+/g, "-").trim() || "ThreeUnityWebBridge";
    const outputFlag = optionValue(args, "--output") ?? optionValue(args, "-o");
    const output = resolve(outputFlag ?? join(project, "Build", safeName, `${safeName}.exe`));
    const unity = await findUnityExecutable(optionValue(args, "--unity"));
    const logicProfile = normalizeLogicProfile(optionValue(args, "--logic-profile"));
    const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "unity-package");
    const webHostProject = resolve(dirname(fileURLToPath(import.meta.url)), "..", "webview-host", "ThreeUnityWebHost.csproj");

    const packageDestination = join(project, "Packages", "com.three-unity.bridge");
    await mkdir(dirname(packageDestination), { recursive: true });
    await rm(packageDestination, { recursive: true, force: true });
    await cp(packageRoot, packageDestination, { recursive: true, force: true });

    const streamingAssets = join(project, "Assets", "StreamingAssets");
    const webDestination = join(streamingAssets, "ThreeUnityWeb");
    const hostDestination = join(streamingAssets, "ThreeUnityWebHost");
    await mkdir(streamingAssets, { recursive: true });
    await rm(webDestination, { recursive: true, force: true });
    await rm(hostDestination, { recursive: true, force: true });
    await cp(webDist, webDestination, { recursive: true, force: true });
    await runProcess("dotnet", [
      "publish", webHostProject, "-c", "Release", "-r", "win-x64",
      "--self-contained", "true", "-p:PublishSingleFile=false", "-p:PublishReadyToRun=false",
      "-o", hostDestination,
    ], "WebView2 host publish");

    const log = join(project, "ThreeUnityWebBridge-build.log");
    const unityArguments = [
      "-batchmode", "-nographics", "-quit",
      "-projectPath", project,
      "-executeMethod", "ThreeUnity.Bridge.Editor.ThreeUnityWebBatchBuilder.BuildFromCommandLine",
      "-threeUnityWebRoot", "ThreeUnityWeb",
      "-threeUnityWebEntry", entry,
      "-threeUnityProductName", productName,
      "-threeUnityOutput", output,
    ];
    if (logicProfile) unityArguments.push("-threeUnityLogicProfile", logicProfile);
    unityArguments.push("-logFile", log);
    await runUnity(unity, unityArguments);
    const built = await stat(output).catch(() => null);
    if (!built?.isFile()) throw new Error(`Unity exited without producing ${output}. See ${log}`);
    console.log(`Built original-web Unity bridge at ${output}`);
    console.log(`Web source copied unchanged from ${webDist}`);
    console.log(`Unity log: ${log}`);
    return;
  }

  throw new Error(`Unknown command '${command}'. Run 'three-unity help'.`);
}

function requireArgument(value: string | undefined, name: string): string {
  if (!value) throw new Error(`Missing ${name}.`);
  return value;
}

function optionValue(args: string[], name: string): string | undefined {
  const index = args.indexOf(name);
  return index >= 0 ? requireArgument(args[index + 1], `${name} value`) : undefined;
}

async function requireUnityProject(project: string): Promise<void> {
  const assets = await stat(join(project, "Assets")).catch(() => null);
  const projectSettings = await stat(join(project, "ProjectSettings")).catch(() => null);
  if (!assets?.isDirectory() || !projectSettings?.isDirectory())
    throw new Error(`${project} is not a Unity project (Assets or ProjectSettings directory not found).`);
}

async function findUnityExecutable(explicit: string | undefined): Promise<string> {
  const configured = explicit ?? process.env.UNITY_EDITOR;
  if (configured) {
    const executable = resolve(configured);
    if ((await stat(executable).catch(() => null))?.isFile()) return executable;
    throw new Error(`Unity executable not found: ${executable}`);
  }
  if (process.platform === "win32") {
    const editorRoot = "C:\\Program Files\\Unity\\Hub\\Editor";
    const versions = await readdir(editorRoot, { withFileTypes: true }).catch(() => []);
    for (const version of versions.filter((entry) => entry.isDirectory()).map((entry) => entry.name).sort().reverse()) {
      const candidate = join(editorRoot, version, "Editor", "Unity.exe");
      if ((await stat(candidate).catch(() => null))?.isFile()) return candidate;
    }
  }
  throw new Error("Unity Editor was not found. Pass --unity <path-to-Unity.exe> or set UNITY_EDITOR.");
}

async function runUnity(executable: string, args: string[]): Promise<void> {
  return runProcess(executable, args, "Unity");
}

async function runProcess(executable: string, args: string[], label: string): Promise<void> {
  await new Promise<void>((resolvePromise, reject) => {
    const child = spawn(executable, args, { windowsHide: true, stdio: "inherit" });
    child.once("error", reject);
    child.once("exit", (code) => code === 0 ? resolvePromise() : reject(new Error(`${label} exited with code ${code}.`)));
  });
}

function printHelp(): void {
  console.log(`three-unity - bridge Three.js scene assets into Unity

Commands:
  validate <scene.threeunity>            Validate a bridge document
  pack <scene.json> [-o output]          Validate and write a .threeunity asset
  install-unity <UnityProject>           Install the UPM package into a project
  copy <scene.threeunity> <UnityProject> Copy an asset into the project's Assets folder
  build-unity <scene> <UnityProject>     Install, generate a playable scene, and build it
      [--unity Unity.exe] [-o output.exe]
  build-web-unity <dist> <UnityProject>  Package the original web game inside Unity
      [--entry index.html] [--name Game]
      [--logic-profile voxel-player-v1|shop-flight-v1]
      [--unity Unity.exe] [-o output.exe]`);
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
