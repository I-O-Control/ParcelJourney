import { build } from "esbuild";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = fileURLToPath(new URL("../../", import.meta.url));
const output = path.join(root, "ParcelJourney.App/Viewer3d");
await mkdir(output, { recursive: true });
// Keep the shipped bundle readable for debugging; edit viewer.js in tools/viewer3d.
await build({
  entryPoints: [path.join(root, "tools/viewer3d/viewer.js")],
  outfile: path.join(output, "viewer.js"),
  bundle: true,
  minify: false,
  format: "iife",
  target: "es2022",
  legalComments: "eof",
});
// Normalize whitespace preserved in dependency comments in the readable bundle.
const bundlePath = path.join(output, "viewer.js");
const bundle = await readFile(bundlePath, "utf8");
await writeFile(
  bundlePath,
  bundle
    .split("\n")
    .map((line) =>
      line
        .replace(/^([ \t]+)/, (indent) => indent.replace(/\t/g, "  "))
        .trimEnd(),
    )
    .join("\n"),
);

// Keep the dataset-agnostic bundled layout geometry; captures are loaded at runtime.
const model = JSON.parse(
  await readFile(
    path.join(root, "ParcelJourney.App/Assets/replay-model.json"),
    "utf8",
  ),
);
if (!Array.isArray(model.nodes) || !Array.isArray(model.edges))
  throw new Error("Layout geometry is required.");
// These English UI metadata fields previously suffered a double UTF-8/1252
// conversion. Reject broken source text before embedding it in a release.
for (const node of model.nodes)
  for (const key of ["label", "note", "coordinateBasis"])
    if (/[\u00c2\u00c3]|\u00e2\u20ac|\ufffd/.test(node[key] || ""))
      throw new Error(`Corrupt layout text: ${node.id}.${key}`);
await writeFile(
  path.join(output, "THREE-LICENSE.txt"),
  await readFile(
    new URL("./node_modules/three/LICENSE", import.meta.url),
    "utf8",
  ),
);
console.log("Bundled viewer assets; parcel captures are selected at runtime.");
