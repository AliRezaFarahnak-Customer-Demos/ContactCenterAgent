/** @type {import('tailwindcss').Config} */
// Norlys brand palette — sourced from https://norlys.design/document/307#/grundelementer/farver
// (with permission from Norlys). Names mirror the official CVI tokens.
export default {
  content: [],
  theme: {
    extend: {
      colors: {
        norlys: {
          // Primary
          red: "#ed0812",
          "red-2": "#d80812",
          "red-3": "#c10000",
          // Secondary — Dark Petroleum
          petroleum: "#0c4c4e",
          "petroleum-2": "#004547",
          "petroleum-3": "#023a3c",
          // Secondary — Light Petroleum
          "light-petroleum": "#dbe7e4",
          "light-petroleum-2": "#d2dfdc",
          "light-petroleum-3": "#c3d4d2",
          // Secondary — Sand (default light surface)
          sand: "#f4f2ec",
          "sand-2": "#ece9e0",
          "sand-3": "#e0dbcd",
          // Text
          ink: "#413f3c", // Warm Grey — primary text on light surfaces
        },
      },
      fontFamily: {
        // Norlys recommends Georgia as the web fallback for "Norlys Headline"
        // and Arial as the fallback for "Norlys Text". The proprietary fonts
        // are gated behind login on norlys.design, so we use the official fallbacks.
        headline: [
          '"Norlys Headline"',
          "Georgia",
          '"Times New Roman"',
          "serif",
        ],
        body: [
          '"Norlys Text"',
          "Arial",
          "Helvetica",
          "system-ui",
          "sans-serif",
        ],
      },
    },
  },
};
