import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";

export default defineConfig({
  clearScreen: false,
  server: {
    port: 1421,
    strictPort: true,
    watch: {
      ignored: ["**/src-tauri/**"],
    },
  },
  build: {
    rollupOptions: {
      input: {
        chat: fileURLToPath(new URL("chat.html", import.meta.url)),
        overlay: fileURLToPath(new URL("overlay.html", import.meta.url)),
      },
    },
  },
});
