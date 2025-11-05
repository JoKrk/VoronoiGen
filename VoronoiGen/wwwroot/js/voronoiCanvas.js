export function prepareCanvas(canvas, cssWidth, cssHeight) {
    const dpr = window.devicePixelRatio || 1;
    canvas.style.width = `${cssWidth}px`;
    canvas.style.height = `${cssHeight}px`;
    const targetW = Math.max(1, Math.floor(cssWidth * dpr));
    const targetH = Math.max(1, Math.floor(cssHeight * dpr));
    if (canvas.width !== targetW) canvas.width = targetW;
    if (canvas.height !== targetH) canvas.height = targetH;
    return { pixelWidth: canvas.width, pixelHeight: canvas.height, dpr };
}

export function clearCanvas(canvas, w, h) {
    const ctx = canvas.getContext('2d');
    ctx.save();
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, w, h);
    ctx.restore();
}

export function drawVoronoi(canvas, data) {
    const ctx = canvas.getContext('2d');

    const {
        pixelWidth, pixelHeight,
        boundsLeft, boundsTop,
        offsetX, offsetY, scale,
        boundary, cells, seeds,
        holes, showHoles,
        showDebug, hudLines
    } = data;

    ctx.save();
    ctx.clearRect(0, 0, pixelWidth, pixelHeight);
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, pixelWidth, pixelHeight);

    // World transform
    ctx.save();
    ctx.translate(offsetX, offsetY);
    ctx.scale(scale, scale);
    ctx.translate(-boundsLeft, -boundsTop);

    // Cells
    if (Array.isArray(cells) && cells.length > 0) {
        ctx.strokeStyle = 'rgba(0,0,0,0.78)';
        ctx.lineWidth = 1 / scale;
        for (let k = 0; k < cells.length; k++) {
            const flat = cells[k];
            if (!flat || flat.length < 6) continue;
            ctx.beginPath();
            ctx.moveTo(flat[0], flat[1]);
            for (let i = 2; i < flat.length; i += 2) ctx.lineTo(flat[i], flat[i + 1]);
            ctx.closePath();
            ctx.stroke();
        }
    }

    // Boundary fill + stroke
    if (Array.isArray(boundary) && boundary.length >= 6) {
        ctx.fillStyle = 'rgba(0,128,255,0.125)';
        ctx.beginPath();
        ctx.moveTo(boundary[0], boundary[1]);
        for (let i = 2; i < boundary.length; i += 2) ctx.lineTo(boundary[i], boundary[i + 1]);
        ctx.closePath();
        ctx.fill();

        ctx.strokeStyle = '#000';
        ctx.lineWidth = 2 / scale;
        ctx.beginPath();
        ctx.moveTo(boundary[0], boundary[1]);
        for (let i = 2; i < boundary.length; i += 2) ctx.lineTo(boundary[i], boundary[i + 1]);
        ctx.closePath();
        ctx.stroke();
    }

    // Holes (internal contours) - draw when showHoles is enabled
    if (showHoles && Array.isArray(holes) && holes.length > 0) {
        ctx.strokeStyle = '#ff0000';
        ctx.lineWidth = 2 / scale;
        for (let h = 0; h < holes.length; h++) {
            const holeFlat = holes[h];
            if (!holeFlat || holeFlat.length < 6) continue;
            ctx.beginPath();
            ctx.moveTo(holeFlat[0], holeFlat[1]);
            for (let i = 2; i < holeFlat.length; i += 2) ctx.lineTo(holeFlat[i], holeFlat[i + 1]);
            ctx.closePath();
            ctx.stroke();
        }
    }

    // Seeds
    if (Array.isArray(seeds) && seeds.length >= 2) {
        ctx.fillStyle = 'red';
        const r = 2 / scale;
        for (let i = 0; i < seeds.length; i += 2) {
            ctx.beginPath();
            ctx.arc(seeds[i], seeds[i + 1], r, 0, Math.PI * 2);
            ctx.fill();
        }
    }

    // HUD in device space
    ctx.restore();
    if (showDebug && Array.isArray(hudLines)) {
        ctx.fillStyle = 'blue';
        ctx.font = '12px monospace';
        let y = 16;
        for (const line of hudLines) {
            ctx.fillText(String(line), 8, y);
            y += 16;
        }
        ctx.strokeStyle = 'rgba(0,0,255,0.25)';
        ctx.lineWidth = 1;
        ctx.strokeRect(0.5, 0.5, pixelWidth - 1, pixelHeight - 1);
    }

    ctx.restore();
}