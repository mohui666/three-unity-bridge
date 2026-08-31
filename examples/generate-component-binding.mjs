import { mkdir, writeFile } from "node:fs/promises";
import { BoxGeometry, Color, Group, Mesh, MeshStandardMaterial, Scene } from "three";
import { exportThreeUnityJson, validateDocument } from "../dist/index.js";

const scene = new Scene();
scene.name = "Component Binding Door";
scene.background = new Color(0x202733);

const frameMaterial = new MeshStandardMaterial({ color: 0x8d7863, roughness: 0.8 });
const doorMaterial = new MeshStandardMaterial({ color: 0x3f6f92, roughness: 0.55 });

const openingWidth = 1.8;
const openingHeight = 2.3;
const frameThickness = 0.22;
const frameDepth = 0.3;

const frame = new Group();
frame.name = "Wall Frame";

const leftJamb = new Mesh(
  new BoxGeometry(frameThickness, openingHeight, frameDepth),
  frameMaterial,
);
leftJamb.name = "Left Jamb";
leftJamb.position.set(-(openingWidth + frameThickness) / 2, openingHeight / 2, 0);
frame.add(leftJamb);

const rightJamb = new Mesh(
  new BoxGeometry(frameThickness, openingHeight, frameDepth),
  frameMaterial,
);
rightJamb.name = "Right Jamb";
rightJamb.position.set((openingWidth + frameThickness) / 2, openingHeight / 2, 0);
frame.add(rightJamb);

const lintel = new Mesh(
  new BoxGeometry(openingWidth + frameThickness * 2, frameThickness, frameDepth),
  frameMaterial,
);
lintel.name = "Lintel";
lintel.position.set(0, openingHeight + frameThickness / 2, 0);
frame.add(lintel);
scene.add(frame);

const doorWidth = 1.65;
const doorHeight = 2.2;
const doorThickness = 0.12;

const doorPivot = new Group();
doorPivot.name = "Door Pivot";
doorPivot.position.set(-doorWidth / 2, doorHeight / 2, 0);
doorPivot.userData.unity = {
  components: [
    {
      type: "Door",
      data: {
        openAngle: 95,
        duration: 0.45,
        startsOpen: false,
      },
    },
  ],
};

const door = new Mesh(
  new BoxGeometry(doorWidth, doorHeight, doorThickness),
  doorMaterial,
);
door.name = "Door";
door.position.x = doorWidth / 2;
doorPivot.add(door);
scene.add(doorPivot);

const json = await exportThreeUnityJson(scene);
const validation = validateDocument(JSON.parse(json));
if (!validation.valid) throw new Error(`Generated component-binding example is invalid: ${validation.errors.join(" ")}`);

await mkdir(new URL("./output/", import.meta.url), { recursive: true });
const output = new URL("./output/component-binding-door.threeunity", import.meta.url);
await writeFile(output, `${json}\n`, "utf8");
console.log(`Wrote ${output.pathname}`);
