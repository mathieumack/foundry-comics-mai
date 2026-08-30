export async function composeComicBoard(title, imageDataUrls, layout) {
    if (!Array.isArray(imageDataUrls) || imageDataUrls.length === 0) {
        throw new Error("The comic board must contain at least one panel.");
    }

    if (!Array.isArray(layout.slots) || layout.slots.length !== imageDataUrls.length) {
        throw new Error("The panel count must match the selected board template.");
    }

    const width = layout.columns * layout.panelSize + (layout.columns - 1) * layout.gutter + 2 * layout.margin;
    const height = layout.titleHeight + layout.rows * layout.panelSize + (layout.rows - 1) * layout.gutter + 2 * layout.margin;
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
        const slot = layout.slots[index];
        const panelWidth = slot.columnSpan * layout.panelSize + (slot.columnSpan - 1) * layout.gutter;
        const panelHeight = slot.rowSpan * layout.panelSize + (slot.rowSpan - 1) * layout.gutter;
        const x = layout.margin + slot.column * (layout.panelSize + layout.gutter);
        const y = layout.titleHeight + layout.margin + slot.row * (layout.panelSize + layout.gutter);
        context.drawImage(image, x, y, panelWidth, panelHeight);
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