import { mkdir, writeFile } from "node:fs/promises";
import { BoxGeometry, Color, DirectionalLight, Mesh, MeshStandardMaterial, PerspectiveCamera, Scene } from "three";
import { exportThreeUnityJson } from "../dist/index.js";

const scene = new Scene();
scene.name = "Three Unity Demo";
scene.background = new Color(0x202733);

const cube = new Mesh(
  new BoxGeometry(1.5, 1.5, 1.5),
  new MeshStandardMaterial({ color: 0x4ca6ff, metalness: 0.15, roughness: 0.35 }),
);
cube.name = "AI Generated Cube";
cube.position.set(0, 1, 0);
cube.userData = {
  gameplayTag: "demo-prop",
  unity: {
    components: [{ type: "Rotator", data: { axis: [0, 1, 0], degreesPerSecond: 30 } }],
  },
};
scene.add(cube);

const camera = new PerspectiveCamera(55, 16 / 9, 0.1, 100);
camera.name = "Demo Camera";
camera.position.set(4, 3, 6);
camera.lookAt(cube.position);
scene.add(camera);

const sun = new DirectionalLight(0xffffff, 2);
sun.name = "Sun";
sun.position.set(3, 5, 2);
sun.castShadow = true;
scene.add(sun);

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/three-unity-demo.threeunity", import.meta.url);
await writeFile(output, `${await exportThreeUnityJson(scene)}\n`, "utf8");
console.log(`Wrote ${output.pathname}`);
