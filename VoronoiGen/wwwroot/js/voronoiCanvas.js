export function getContainerSize(container) {
    const rect = container.getBoundingClientRect();
    return { width: Math.floor(rect.width), height: Math.floor(rect.height) };
}

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
        boundary, originalBoundary,
        cells, cellsBezier,
        holes,                 // holes are always polylines
        seeds,
        showHoles, splineMode,
        showDebug, hudLines
    } = data;

    ctx.save();
    ctx.clearRect(0, 0, pixelWidth, pixelHeight);
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, pixelWidth, pixelHeight);

    ctx.save();
    ctx.translate(offsetX, offsetY);
    ctx.scale(scale, scale);
    ctx.translate(-boundsLeft, -boundsTop);

    // Cells (polyline vs spline)
    ctx.strokeStyle = 'rgba(0,0,0,0.78)';
    ctx.lineWidth = 1 / scale;

    if (!splineMode && Array.isArray(cells) && cells.length > 0) {
        for (const flat of cells) {
            if (!flat || flat.length < 6) continue;
            ctx.beginPath();
            ctx.moveTo(flat[0], flat[1]);
            for (let i = 2; i < flat.length; i += 2) ctx.lineTo(flat[i], flat[i + 1]);
            ctx.closePath();
            ctx.stroke();
        }
    } else if (splineMode && Array.isArray(cellsBezier) && cellsBezier.length > 0) {
        for (const bez of cellsBezier) {
            if (!bez || bez.length < 8) continue;
            ctx.beginPath();
            ctx.moveTo(bez[0], bez[1]);
            // groups of 6: c1x,c1y,c2x,c2y,p3x,p3y
            for (let i = 2; i + 5 < bez.length; i += 6) {
                ctx.bezierCurveTo(
                    bez[i], bez[i + 1],
                    bez[i + 2], bez[i + 3],
                    bez[i + 4], bez[i + 5]
                );
            }
            ctx.closePath();
            ctx.stroke();
        }
    }

    const hasOriginal = Array.isArray(originalBoundary) && originalBoundary.length >= 6;
    const hasBoundary = Array.isArray(boundary) && boundary.length >= 6;

    // Boundary layers
    if (hasOriginal) {
        // Original fill + stroke
        ctx.fillStyle = 'rgba(0,128,255,0.125)';
        ctx.beginPath();
        ctx.moveTo(originalBoundary[0], originalBoundary[1]);
        for (let i = 2; i < originalBoundary.length; i += 2) ctx.lineTo(originalBoundary[i], originalBoundary[i + 1]);
        ctx.closePath();
        ctx.fill();

        ctx.strokeStyle = '#000';
        ctx.lineWidth = 2 / scale;
        ctx.beginPath();
        ctx.moveTo(originalBoundary[0], originalBoundary[1]);
        for (let i = 2; i < originalBoundary.length; i += 2) ctx.lineTo(originalBoundary[i], originalBoundary[i + 1]);
        ctx.closePath();
        ctx.stroke();

        if (hasBoundary) {
            ctx.save();
            ctx.setLineDash([6 / scale, 4 / scale]);
            ctx.beginPath();
            ctx.moveTo(boundary[0], boundary[1]);
            for (let i = 2; i < boundary.length; i += 2) ctx.lineTo(boundary[i], boundary[i + 1]);
            ctx.closePath();
            ctx.stroke();
            ctx.restore();
        }
    } else if (hasBoundary) {
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

    // Holes (always polylines)
    if (showHoles && Array.isArray(holes) && holes.length > 0) {
        ctx.strokeStyle = '#ff0000';
        ctx.lineWidth = 2 / scale;
        for (const flat of holes) {
            if (!flat || flat.length < 6) continue;
            ctx.beginPath();
            ctx.moveTo(flat[0], flat[1]);
            for (let i = 2; i < flat.length; i += 2) ctx.lineTo(flat[i], flat[i + 1]);
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