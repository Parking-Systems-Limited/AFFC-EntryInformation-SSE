/** Vehicle payload from SSE (English keys). */
export interface VehiclePayload {
  photo?: string | null
  lpn?: string | null
  entryTime?: string | null
  identity?: string | null
  vehicleType?: string | null
  warehouseDestination?: string | null
  approvalStatus?: string | null
  paymentStatus?: string | null
  paymentTime?: string | null
  hasBooking?: boolean | null
  bookingId?: string | null
  [key: string]: unknown
}

export interface VehicleInfo {
  photo: string
  lpn: string
  entryTime: string
  identity: string
  vehicleType: string
  warehouseDestination: string
  approvalStatus: string
  paymentStatus: string
  paymentTime: string
  hasBooking: boolean
  bookingId: string
}

export const EMPTY_VEHICLE: VehicleInfo = {
  photo: '',
  lpn: '',
  entryTime: '',
  identity: '',
  vehicleType: '',
  warehouseDestination: '',
  approvalStatus: '',
  paymentStatus: '',
  paymentTime: '',
  hasBooking: false,
  bookingId: '',
}

function pick(payload: VehiclePayload, ...keys: string[]): string {
  for (const key of keys) {
    const value = payload[key]
    if (value != null && String(value).trim() !== '') {
      return String(value)
    }
  }
  return ''
}

export function normalizeVehicle(payload: VehiclePayload): VehicleInfo {
  const hasBookingRaw = payload.hasBooking
  const hasBooking =
    typeof hasBookingRaw === 'boolean'
      ? hasBookingRaw
      : String(hasBookingRaw ?? '').toLowerCase() === 'true' ||
        pick(payload, 'bookingId') !== ''

  return {
    photo: pick(payload, 'photo'),
    lpn: pick(payload, 'lpn', '車牌'),
    entryTime: pick(payload, 'entryTime', '入場時間'),
    identity: pick(payload, 'identity', '身份'),
    vehicleType: pick(payload, 'vehicleType', '車輛種類'),
    warehouseDestination: pick(payload, 'warehouseDestination', '前往貨倉'),
    approvalStatus: pick(payload, 'approvalStatus', '批核狀態'),
    paymentStatus: pick(payload, 'paymentStatus', '付款狀態'),
    paymentTime: pick(payload, 'paymentTime', '付款時間'),
    hasBooking,
    bookingId: pick(payload, 'bookingId'),
  }
}
