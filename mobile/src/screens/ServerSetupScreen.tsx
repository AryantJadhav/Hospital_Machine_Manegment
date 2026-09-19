import { useCallback, useRef, useState } from 'react';
import {
  ActivityIndicator,
  StyleSheet,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import { api, setServerUrl } from '../api';
import { normaliseServerUrl, storage } from '../storage';
import { theme } from '../theme';

/**
 * First screen on a fresh install: where is the server?
 *
 * There is no cloud to fall back on. The app has to be told the address of
 * a box on the hospital's own LAN, so this is the one piece of setup that
 * cannot be skipped.
 *
 * Two paths:
 *   1. Scan a QR code shown on the server's setup or diagnostics page.
 *   2. Type the address by hand — for when the admin is on the phone, or
 *      the QR is not available.
 */
export function ServerSetupScreen({ onDone }: { onDone: (url: string) => void }) {
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [permission, requestPermission] = useCameraPermissions();
  const locked = useRef(false);

  async function connectTo(raw: string) {
    const url = normaliseServerUrl(raw);
    if (!url) {
      setError('Enter an address like 192.168.1.50 or hospitalpm.local');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      // Probe before saving. Storing an unreachable address just moves the
      // failure to the login screen, where the cause is less obvious.
      await api.health(url);
      await storage.setServerUrl(url);
      setServerUrl(url);
      onDone(url);
    } catch {
      setError(
        `Could not reach ${url}. Check the device is on the hospital Wi-Fi and the address is right.`,
      );
    } finally {
      setBusy(false);
    }
  }

  const onScanned = useCallback(
    ({ data }: { data: string }) => {
      if (locked.current || busy) return;
      locked.current = true;
      setScanning(false);
      void connectTo(data).finally(() => {
        setTimeout(() => {
          locked.current = false;
        }, 1200);
      });
    },
    [busy],
  );

  async function startScanning() {
    if (!permission?.granted) {
      const result = await requestPermission();
      if (!result.granted) {
        setError('Camera permission is needed to scan the QR code.');
        return;
      }
    }
    setError(null);
    setScanning(true);
  }

  // --- QR scanner view ---
  if (scanning) {
    return (
      <View style={styles.container}>
        <CameraView
          style={StyleSheet.absoluteFill}
          facing="back"
          barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
          onBarcodeScanned={onScanned}
        />
        <View style={styles.scanOverlay} pointerEvents="box-none">
          <View style={styles.reticle} />
          <Text style={styles.scanHint}>
            Point at the QR code on the server&rsquo;s screen
          </Text>
          <TouchableOpacity
            style={styles.cancelButton}
            onPress={() => setScanning(false)}
          >
            <Text style={styles.cancelText}>Type it instead</Text>
          </TouchableOpacity>
        </View>
      </View>
    );
  }

  // --- Manual entry view ---
  return (
    <View style={styles.container}>
      <Text style={styles.title}>Hospital PM</Text>
      <Text style={styles.subtitle}>Connect this device to your hospital&rsquo;s server.</Text>

      <TouchableOpacity
        style={[styles.scanButton, busy && styles.buttonDisabled]}
        onPress={() => void startScanning()}
        disabled={busy}
      >
        <Text style={styles.scanButtonText}>📷  Scan server QR code</Text>
      </TouchableOpacity>

      <View style={styles.divider}>
        <View style={styles.dividerLine} />
        <Text style={styles.dividerText}>or enter the address</Text>
        <View style={styles.dividerLine} />
      </View>

      <Text style={styles.label}>Server address</Text>
      <TextInput
        style={styles.input}
        value={value}
        onChangeText={setValue}
        placeholder="192.168.1.50 or hospitalpm.local"
        placeholderTextColor={theme.muted}
        autoCapitalize="none"
        autoCorrect={false}
        keyboardType="url"
        editable={!busy}
      />

      <Text style={styles.hint}>
        The QR code is shown on the server&rsquo;s setup screen and diagnostics page.
      </Text>

      {error ? <Text style={styles.error}>{error}</Text> : null}

      <TouchableOpacity
        style={[styles.button, busy && styles.buttonDisabled]}
        onPress={() => void connectTo(value)}
        disabled={busy}
      >
        {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Connect</Text>}
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, justifyContent: 'center', padding: 24, backgroundColor: theme.bg },
  title: { fontSize: 26, fontWeight: '700', color: theme.text },
  subtitle: { fontSize: 15, color: theme.muted, marginTop: 4, marginBottom: 28 },
  label: { fontSize: 13, color: theme.muted, marginBottom: 6 },
  input: {
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 8,
    paddingHorizontal: 14,
    paddingVertical: 12,
    fontSize: 16,
    color: theme.text,
    backgroundColor: theme.surface,
  },
  hint: { fontSize: 12, color: theme.muted, marginTop: 8 },
  error: { color: theme.danger, marginTop: 14, fontSize: 14 },
  button: {
    backgroundColor: theme.accent,
    borderRadius: 8,
    paddingVertical: 15,
    alignItems: 'center',
    marginTop: 24,
  },
  buttonDisabled: { opacity: 0.6 },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },

  // Scan QR button
  scanButton: {
    backgroundColor: theme.accent,
    borderRadius: 8,
    paddingVertical: 16,
    alignItems: 'center',
    marginBottom: 4,
  },
  scanButtonText: { color: '#fff', fontSize: 16, fontWeight: '600' },

  // Divider
  divider: {
    flexDirection: 'row',
    alignItems: 'center',
    marginVertical: 20,
  },
  dividerLine: {
    flex: 1,
    height: 1,
    backgroundColor: theme.border,
  },
  dividerText: {
    marginHorizontal: 12,
    fontSize: 13,
    color: theme.muted,
  },

  // Camera overlay
  scanOverlay: {
    ...StyleSheet.absoluteFill,
    justifyContent: 'center',
    alignItems: 'center',
  },
  reticle: {
    width: 220,
    height: 220,
    borderWidth: 2,
    borderColor: 'rgba(255,255,255,0.7)',
    borderRadius: 16,
  },
  scanHint: {
    color: '#fff',
    fontSize: 15,
    marginTop: 16,
    textShadowColor: 'rgba(0,0,0,0.6)',
    textShadowOffset: { width: 0, height: 1 },
    textShadowRadius: 4,
  },
  cancelButton: {
    marginTop: 24,
    paddingVertical: 12,
    paddingHorizontal: 24,
    backgroundColor: 'rgba(0,0,0,0.5)',
    borderRadius: 8,
  },
  cancelText: {
    color: '#fff',
    fontSize: 15,
  },
});
