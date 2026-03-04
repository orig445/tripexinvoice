import * as pdfjsLib from "pdfjs-dist";

// Use the bundled worker
pdfjsLib.GlobalWorkerOptions.workerSrc = `https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.9.155/pdf.worker.min.mjs`;

/**
 * Renders the first page of a PDF file to a JPEG base64 data URL.
 * Returns { base64DataUrl, blob } for upload/preview.
 */
export async function pdfPageToImage(
  file: File,
  maxWidth = 1600,
  quality = 0.85
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

  const base64DataUrl = canvas.toDataURL("image/jpeg", quality);

  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => {
        if (blob) resolve({ base64DataUrl, blob });
        else reject(new Error("Failed to convert PDF to image"));
      },
      "image/jpeg",
      quality
    );
  });
}

/**
 * Renders ALL pages of a PDF to JPEG base64 strings (without data: prefix).
 * Used by chatbot to send multiple pages for analysis.
 */
export async function pdfAllPagesToBase64(
  file: File,
  maxWidth = 1200,
  quality = 0.7
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
    const dataUrl = canvas.toDataURL("image/jpeg", quality);
    const base64 = dataUrl.split(",")[1];
    if (base64) pages.push(base64);
  }

  return pages;
}
