import { useCallback, useEffect, useRef, useState } from 'react';
import type jsQRType from 'jsqr';

/**
 * Reads QR codes from a webcam.
 *
 * Two decoders. BarcodeDetector is native, hardware-accelerated and costs
 * nothing to ship, but it only exists in Chromium. jsQR is the fallback for
 * Firefox and Safari, which have no barcode API at all — a hospital that
 * standardised on Firefox should not simply lose the feature.
 */

type BarcodeDetectorLike = {
  detect: (source: CanvasImageSource) => Promise<{ rawValue: string }[]>;
};

type BarcodeDetectorCtor = new (options?: { formats?: string[] }) => BarcodeDetectorLike;

export type ScannerState =
  | { kind: 'idle' }
  | { kind: 'insecure' }
  | { kind: 'unsupported' }
  | { kind: 'denied' }
  | { kind: 'no-camera' }
  | { kind: 'starting' }
  | { kind: 'scanning' }
  | { kind: 'error'; message: string };

export type CameraOption = { deviceId: string; label: string };

export function useQrScanner(onDecoded: (value: string) => void) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const frameRef = useRef<number | null>(null);
  const detectorRef = useRef<BarcodeDetectorLike | null>(null);
  const jsQrRef = useRef<typeof jsQRType | null>(null);
  const busyRef = useRef(false);

  const [state, setState] = useState<ScannerState>({ kind: 'idle' });
  const [cameras, setCameras] = useState<CameraOption[]>([]);

  // Held in a ref so restarting the camera does not depend on the callback's
  // identity, which changes on every parent render.
  const onDecodedRef = useRef(onDecoded);
  useEffect(() => {
    onDecodedRef.current = onDecoded;
  }, [onDecoded]);

  const stop = useCallback(() => {
    if (frameRef.current !== null) {
      cancelAnimationFrame(frameRef.current);
      frameRef.current = null;
    }
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
    busyRef.current = false;
  }, []);

  const start = useCallback(
    async (deviceId?: string) => {
      // getUserMedia only exists in a secure context. On a hospital LAN over
      // plain HTTP, navigator.mediaDevices is undefined outright — the check
      // has to come first or the failure looks like a broken camera.
      if (!window.isSecureContext) {
        setState({ kind: 'insecure' });
        return;
      }

      if (!navigator.mediaDevices?.getUserMedia) {
        setState({ kind: 'unsupported' });
        return;
      }

      setState({ kind: 'starting' });

      try {
        const stream = await navigator.mediaDevices.getUserMedia({
          video: deviceId
            ? { deviceId: { exact: deviceId } }
            : { facingMode: 'environment', width: { ideal: 1280 } },
          audio: false,
        });

        streamRef.current = stream;

        // Labels are blank until permission is granted, so the device list is
        // only worth reading after getUserMedia has succeeded.
        const devices = await navigator.mediaDevices.enumerateDevices();
        setCameras(
          devices
            .filter((d) => d.kind === 'videoinput')
            .map((d, i) => ({ deviceId: d.deviceId, label: d.label || `Camera ${i + 1}` })),
        );

        const video = videoRef.current;
        if (!video) return;

        video.srcObject = stream;
        video.setAttribute('playsinline', 'true');
        await video.play();

        const Ctor = (window as unknown as { BarcodeDetector?: BarcodeDetectorCtor })
          .BarcodeDetector;
        detectorRef.current = Ctor ? new Ctor({ formats: ['qr_code'] }) : null;

        // jsQR is ~138 kB and only Firefox and Safari need it. Loading it
        // lazily keeps that weight off every Chromium user, which on a
        // hospital LAN is most of them and the first load is over Wi-Fi.
        if (!detectorRef.current && !jsQrRef.current) {
          jsQrRef.current = (await import('jsqr')).default;
        }

        setState({ kind: 'scanning' });
        frameRef.current = requestAnimationFrame(tick);
      } catch (err) {
        const name = err instanceof DOMException ? err.name : '';
        if (name === 'NotAllowedError' || name === 'SecurityError') setState({ kind: 'denied' });
        else if (name === 'NotFoundError' || name === 'OverconstrainedError') setState({ kind: 'no-camera' });
        else setState({ kind: 'error', message: err instanceof Error ? err.message : 'Camera failed.' });
      }
    },
    // tick is stable via refs; excluding it keeps start from being recreated.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  const tick = useCallback(async () => {
    const video = videoRef.current;
    const canvas = canvasRef.current;

    if (!video || !canvas || video.readyState !== video.HAVE_ENOUGH_DATA) {
      frameRef.current = requestAnimationFrame(tick);
      return;
    }

    if (busyRef.current) {
      frameRef.current = requestAnimationFrame(tick);
      return;
    }

    busyRef.current = true;
    try {
      const width = video.videoWidth;
      const height = video.videoHeight;
      canvas.width = width;
      canvas.height = height;

      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      if (!ctx) return;

      ctx.drawImage(video, 0, 0, width, height);

      let value: string | null = null;

      if (detectorRef.current) {
        const results = await detectorRef.current.detect(canvas);
        value = results[0]?.rawValue ?? null;
      } else if (jsQrRef.current) {
        const image = ctx.getImageData(0, 0, width, height);
        // dontInvert is the cheapest mode. Asset labels are printed dark on
        // light, so the inverted passes only cost frame rate.
        value =
          jsQrRef.current(image.data, width, height, { inversionAttempts: 'dontInvert' })?.data ??
          null;
      }

      if (value) {
        onDecodedRef.current(value);
      }
    } catch {
      // A single bad frame is not worth tearing the camera down for.
    } finally {
      busyRef.current = false;
      frameRef.current = requestAnimationFrame(tick);
    }
  }, []);

  useEffect(() => stop, [stop]);

  return { videoRef, canvasRef, state, cameras, start, stop, setState };
}
