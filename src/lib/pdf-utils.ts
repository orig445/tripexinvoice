import * as pdfjsLib from "pdfjs-dist";
import pdfjsWorker from "pdfjs-dist/build/pdf.worker.min.mjs?url";

// Always use the worker from the same installed pdfjs-dist version (prevents API/worker mismatch)
pdfjsLib.GlobalWorkerOptions.workerSrc = pdfjsWorker;

/**
 * Renders the first page of a PDF file to a PNG base64 data URL.
 * PNG is used instead of JPEG to avoid JPEG encoding variant issues with vision APIs.
 * Returns { base64DataUrl, blob } for upload/preview.
 */
export async function pdfPageToImage(
  file: File,
  maxWidth = 1600
): Promise<{ base64DataUrl: string; blob: Blob }> {
  const arrayBuffer = await file.arrayBuffer();
  const pdf = await pdfjsLib.getDocument({ data: arrayBuffer }).promise;
  const page = await pdf.getPage(1);

  const unscaledViewport = page.getViewport({ scale: 1 });
  const scale = Math.min(1, maxWidth / unscaledViewport.width) * 2; // 2x for quality
  const viewport = page.getViewport({ scale });

  const canvas = document.createElement("canvas");
  canvas.width = viewport.width;
  canvas.height = viewport.height;
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("Canvas not supported");

  await page.render({ canvasContext: ctx, viewport }).promise;

  const base64DataUrl = canvas.toDataURL("image/png");

  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => {
        if (blob) resolve({ base64DataUrl, blob });
        else reject(new Error("Failed to convert PDF to image"));
      },
      "image/png"
    );
  });
}

/**
 * Renders ALL pages of a PDF to PNG base64 strings (without data: prefix).
 * Used by chatbot to send multiple pages for analysis.
 */
export async function pdfAllPagesToBase64(
  file: File,
  maxWidth = 1200
): Promise<string[]> {
  const arrayBuffer = await file.arrayBuffer();
  const pdf = await pdfjsLib.getDocument({ data: arrayBuffer }).promise;
  const pages: string[] = [];

  for (let i = 1; i <= Math.min(pdf.numPages, 5); i++) {
    const page = await pdf.getPage(i);
    const unscaledViewport = page.getViewport({ scale: 1 });
    const scale = Math.min(1, maxWidth / unscaledViewport.width) * 2;
    const viewport = page.getViewport({ scale });

    const canvas = document.createElement("canvas");
    canvas.width = viewport.width;
    canvas.height = viewport.height;
    const ctx = canvas.getContext("2d");
    if (!ctx) continue;

    await page.render({ canvasContext: ctx, viewport }).promise;
    const dataUrl = canvas.toDataURL("image/png");
    const base64 = dataUrl.split(",")[1];
    if (base64) pages.push(base64);
  }

  return pages;
}
