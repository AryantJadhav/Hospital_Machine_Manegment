import { ScrollView, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { STATUS_LABEL, type Equipment } from '../api';
import { theme } from '../theme';

/**
 * What the technician sees after a scan.
 *
 * Ordered by what someone standing in front of the machine actually needs:
 * which machine is this, where should it be, and is it still in warranty.
 * Nothing here is patient data — an asset record never carries any.
 */
export function EquipmentDetailScreen({
  equipment,
  onBack,
}: {
  equipment: Equipment;
  onBack: () => void;
}) {
  const warranty = parseDate(equipment.warrantyExpiryDate);
  const inWarranty = warranty !== null && warranty.getTime() >= Date.now();

  return (
    <View style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <Text style={styles.tag}>{equipment.assetTag}</Text>
        <Text style={styles.type}>{equipment.equipmentTypeName}</Text>

        <View style={styles.badges}>
          <Badge
            label={STATUS_LABEL[equipment.status] ?? 'Unknown'}
            tone={equipment.status === 20 ? 'ok' : equipment.status >= 40 ? 'danger' : 'warn'}
          />
          {warranty ? (
            <Badge
              label={inWarranty ? `In warranty to ${formatDate(warranty)}` : `Warranty expired ${formatDate(warranty)}`}
              tone={inWarranty ? 'ok' : 'warn'}
            />
          ) : null}
        </View>

        <Section title="Location">
          <Field label="Where" value={equipment.locationName} strong />
        </Section>

        <Section title="Identification">
          <Field label="Serial number" value={equipment.serialNumber} mono />
          <Field label="Manufacturer" value={equipment.manufacturer} />
          <Field label="Model" value={equipment.model} />
        </Section>

        <Section title="Dates">
          <Field label="Installed" value={formatIso(equipment.installationDate)} />
          <Field label="Purchased" value={formatIso(equipment.purchaseDate)} />
          <Field label="Warranty expiry" value={formatIso(equipment.warrantyExpiryDate)} />
        </Section>

        {equipment.notes ? (
          <Section title="Notes">
            <Text style={styles.notes}>{equipment.notes}</Text>
          </Section>
        ) : null}
      </ScrollView>

      <TouchableOpacity style={styles.button} onPress={onBack}>
        <Text style={styles.buttonText}>Scan another</Text>
      </TouchableOpacity>
    </View>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.section}>
      <Text style={styles.sectionTitle}>{title}</Text>
      <View style={styles.card}>{children}</View>
    </View>
  );
}

function Field({
  label,
  value,
  mono,
  strong,
}: {
  label: string;
  value: string | null | undefined;
  mono?: boolean;
  strong?: boolean;
}) {
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <Text
        style={[
          styles.fieldValue,
          mono ? styles.mono : null,
          strong ? styles.strong : null,
          !value ? styles.empty : null,
        ]}
      >
        {value && value.length > 0 ? value : '—'}
      </Text>
    </View>
  );
}

function Badge({ label, tone }: { label: string; tone: 'ok' | 'warn' | 'danger' }) {
  const background = tone === 'ok' ? '#17351f' : tone === 'danger' ? '#3a1a1c' : '#3a2f14';
  const color = tone === 'ok' ? theme.ok : tone === 'danger' ? theme.danger : '#e0b071';

  return (
    <View style={[styles.badge, { backgroundColor: background }]}>
      <Text style={[styles.badgeText, { color }]}>{label}</Text>
    </View>
  );
}

function parseDate(iso: string | null): Date | null {
  if (!iso) return null;
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? null : d;
}

/**
 * Day-first, matching how every date in an Indian hospital is written. The
 * server stores dates unambiguously; only the display is localised.
 */
function formatDate(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}

function formatIso(iso: string | null): string | null {
  const d = parseDate(iso);
  return d ? formatDate(d) : null;
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: theme.bg },
  content: { padding: 20, paddingBottom: 30 },
  tag: { fontSize: 30, fontWeight: '700', color: theme.text, letterSpacing: -0.5 },
  type: { fontSize: 17, color: theme.muted, marginTop: 2 },
  badges: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 14 },
  badge: { paddingVertical: 6, paddingHorizontal: 12, borderRadius: 999 },
  badgeText: { fontSize: 13, fontWeight: '600' },
  section: { marginTop: 24 },
  sectionTitle: {
    fontSize: 12,
    color: theme.muted,
    textTransform: 'uppercase',
    letterSpacing: 0.6,
    marginBottom: 8,
  },
  card: {
    backgroundColor: theme.surface,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: theme.border,
    paddingHorizontal: 14,
  },
  field: { paddingVertical: 12, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: theme.border },
  fieldLabel: { fontSize: 12, color: theme.muted, marginBottom: 3 },
  fieldValue: { fontSize: 16, color: theme.text },
  strong: { fontSize: 19, fontWeight: '600' },
  mono: { fontFamily: 'monospace' },
  empty: { color: theme.muted },
  notes: { fontSize: 15, color: theme.text, paddingVertical: 12, lineHeight: 21 },
  button: {
    backgroundColor: theme.accent,
    margin: 20,
    marginTop: 0,
    borderRadius: 8,
    paddingVertical: 16,
    alignItems: 'center',
  },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
});
