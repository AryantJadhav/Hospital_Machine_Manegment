import { useEffect, useRef } from 'react';
import QRCode from 'qrcode';

/**
 * Renders a QR code encoding the server URL so a technician can point their
 * phone at the screen instead of typing an IP address.
 *
 * Uses a canvas element — QRCode.toCanvas is the smallest path through the
 * library and avoids creating a data URL or an SVG string that React would
 * have to dangerouslySetInnerHTML.
 */
export function ServerQrCode({ url, size = 200 }: { url: string; size?: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    QRCode.toCanvas(canvas, url, {
      width: size,
      margin: 2,
      color: { dark: '#1b2430', light: '#ffffff' },
      errorCorrectionLevel: 'M',
    }).catch(() => {
      // If the URL is somehow invalid, leave the canvas blank rather than
      // crashing the page.
    });
  }, [url, size]);

  return (
    <div className="qr-connect">
      <canvas ref={canvasRef} />
      <p className="mono qr-url">{url}</p>
      <p className="muted qr-hint">
        Scan this with the Hospital PM app to connect a phone to this server.
      </p>
    </div>
  );
}
