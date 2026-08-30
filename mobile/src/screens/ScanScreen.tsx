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
import { api, ApiError, extractAssetTag, type Equipment } from '../api';
import { theme } from '../theme';

/**
 * Scan a tag, or type it when the label is too scratched to read.
 *
 * The manual entry path is not a fallback nobody uses — asset labels live on
 * machines wiped with disinfectant several times a day, and after a couple
 * of years a meaningful share of them will not scan at all. A scanner
 * without manual entry sends the technician back to the office.
 */
export function ScanScreen({
  onFound,
  onSignOut,
}: {
  onFound: (equipment: Equipment) => void;
  onSignOut: () => void;
}) {
  const [permission, requestPermission] = useCameraPermissions();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [manual, setManual] = useState('');
  const [showManual, setShowManual] = useState(false);

  // A camera fires the same barcode many times a second. Without this guard
  // one scan becomes dozens of identical requests.
  const locked = useRef(false);

  const lookup = useCallback(
    async (rawValue: string) => {
      const tag = extractAssetTag(rawValue);
      if (!tag) return;

      setBusy(true);
      setError(null);
      try {
        onFound(await api.byTag(tag));
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) {
          setError(`No equipment with tag "${tag}". It may not be on the register yet.`);
        } else {
          setError(e instanceof Error ? e.message : 'Lookup failed.');
        }
      } finally {
        setBusy(false);
        // Re-armed after a short delay rather than immediately, so the same
        // label still in frame does not instantly re-trigger.
        setTimeout(() => {
          locked.current = false;
        }, 1200);
      }
    },
    [onFound],
  );

  if (!permission) {
    return (
      <View style={styles.centre}>
        <ActivityIndicator color={theme.accent} />
      </View>
    );
  }

  if (!permission.granted) {
    return (
      <View style={styles.centre}>
        <Text style={styles.title}>Camera access</Text>
        <Text style={styles.body}>
          Hospital PM uses the camera only to read asset tags. Nothing is recorded or uploaded.
        </Text>
        <TouchableOpacity style={styles.button} onPress={() => void requestPermission()}>
          <Text style={styles.buttonText}>Allow camera</Text>
        </TouchableOpacity>
        <TouchableOpacity style={styles.linkButton} onPress={() => setShowManual(true)}>
          <Text style={styles.link}>Enter a tag by hand instead</Text>
        </TouchableOpacity>
        {showManual ? <ManualEntry value={manual} onChange={setManual} onSubmit={lookup} busy={busy} /> : null}
      </View>
    );
  }

  return (
    <View style={styles.container}>
      <CameraView
        style={StyleSheet.absoluteFill}
        facing="back"
        barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
        onBarcodeScanned={({ data }) => {
          if (locked.current || busy) return;
          locked.current = true;
          void lookup(data);
        }}
      />

      <View style={styles.overlay} pointerEvents="box-none">
        <View style={styles.reticle} />
        <Text style={styles.hint}>Point the camera at the asset tag</Text>

        {busy ? (
          <View style={styles.status}>
            <ActivityIndicator color="#fff" />
            <Text style={styles.statusText}>Looking up…</Text>
          </View>
        ) : null}

        {error ? (
          <View style={styles.errorBox}>
            <Text style={styles.errorText}>{error}</Text>
          </View>
        ) : null}

        <View style={styles.bottom}>
          <TouchableOpacity style={styles.secondary} onPress={() => setShowManual((s) => !s)}>
            <Text style={styles.secondaryText}>
              {showManual ? 'Hide manual entry' : 'Type tag instead'}
            </Text>
          </TouchableOpacity>

          <TouchableOpacity style={styles.secondary} onPress={onSignOut}>
            <Text style={styles.secondaryText}>Sign out</Text>
          </TouchableOpacity>
        </View>

        {showManual ? (
          <ManualEntry value={manual} onChange={setManual} onSubmit={lookup} busy={busy} />
        ) : null}
      </View>
    </View>
  );
}

function ManualEntry({
  value,
  onChange,
  onSubmit,
  busy,
}: {
  value: string;
  onChange: (v: string) => void;
  onSubmit: (v: string) => void | Promise<void>;
  busy: boolean;
}) {
  return (
    <View style={styles.manual}>
      <TextInput
        style={styles.input}
        value={value}
        onChangeText={onChange}
        placeholder="Asset tag, e.g. BME-0001"
        placeholderTextColor={theme.muted}
        autoCapitalize="characters"
        autoCorrect={false}
        onSubmitEditing={() => void onSubmit(value)}
        returnKeyType="search"
      />
      <TouchableOpacity
        style={[styles.button, busy && styles.buttonDisabled]}
        onPress={() => void onSubmit(value)}
        disabled={busy || value.trim().length === 0}
      >
        <Text style={styles.buttonText}>Find</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#000' },
  centre: { flex: 1, justifyContent: 'center', padding: 24, backgroundColor: theme.bg },
  overlay: { flex: 1, justifyContent: 'center', alignItems: 'center', padding: 20 },
  reticle: {
    width: 230,
    height: 230,
    borderWidth: 3,
    borderColor: 'rgba(255,255,255,0.85)',
    borderRadius: 16,
  },
  hint: { color: '#fff', marginTop: 16, fontSize: 15 },
  title: { fontSize: 22, fontWeight: '700', color: theme.text, marginBottom: 8 },
  body: { fontSize: 15, color: theme.muted, marginBottom: 20, lineHeight: 21 },
  status: { flexDirection: 'row', alignItems: 'center', marginTop: 18, gap: 10 },
  statusText: { color: '#fff', fontSize: 15 },
  errorBox: {
    marginTop: 18,
    backgroundColor: 'rgba(229,72,77,0.92)',
    padding: 12,
    borderRadius: 8,
  },
  errorText: { color: '#fff', fontSize: 14 },
  bottom: { position: 'absolute', bottom: 28, flexDirection: 'row', gap: 12 },
  secondary: {
    backgroundColor: 'rgba(0,0,0,0.6)',
    paddingVertical: 12,
    paddingHorizontal: 16,
    borderRadius: 8,
  },
  secondaryText: { color: '#fff', fontSize: 15 },
  manual: {
    position: 'absolute',
    bottom: 86,
    left: 20,
    right: 20,
    backgroundColor: theme.surface,
    borderRadius: 10,
    padding: 12,
    gap: 10,
  },
  input: {
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 12,
    fontSize: 16,
    color: theme.text,
  },
  button: { backgroundColor: theme.accent, borderRadius: 8, paddingVertical: 14, alignItems: 'center' },
  buttonDisabled: { opacity: 0.5 },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  linkButton: { marginTop: 16, alignItems: 'center' },
  link: { color: theme.accent, fontSize: 15 },
});
