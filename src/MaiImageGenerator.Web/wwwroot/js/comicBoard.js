export async function composeComicBoard(title, imageDataUrls, layout) {
    if (!Array.isArray(imageDataUrls) || imageDataUrls.length === 0) {
        throw new Error("The comic board must contain at least one panel.");
    }

    const columns = Math.min(imageDataUrls.length, 2);
    const rows = Math.ceil(imageDataUrls.length / columns);
    const width = columns * layout.panelSize + (columns - 1) * layout.gutter + 2 * layout.margin;
    const height = layout.titleHeight + rows * layout.panelSize + (rows - 1) * layout.gutter + 2 * layout.margin;
    const canvas = document.createElement("canvas");
    const context = canvas.getContext("2d");

    canvas.width = width;
    canvas.height = height;
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, width, height);
    context.fillStyle = "#111713";
    context.font = "700 52px Georgia, serif";
    context.textBaseline = "top";
    context.fillText(title || "Untitled comic board", layout.margin, layout.margin, width - 2 * layout.margin);

    const images = await Promise.all(imageDataUrls.map(loadImage));
    images.forEach((image, index) => {
        const column = index % columns;
        const row = Math.floor(index / columns);
        const x = layout.margin + column * (layout.panelSize + layout.gutter);
        const y = layout.titleHeight + layout.margin + row * (layout.panelSize + layout.gutter);
        context.drawImage(image, x, y, layout.panelSize, layout.panelSize);
    });

    return canvas.toDataURL("image/png");
}

function loadImage(source) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = () => reject(new Error("A generated panel could not be loaded for assembly."));
        image.src = source;
    });
}