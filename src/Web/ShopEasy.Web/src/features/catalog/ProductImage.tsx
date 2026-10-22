import { safeImageSrc } from './imageUrl'

// No image, or one that is not https (see safeImageSrc), gets a plain block with the first letter
export default function ProductImage({ name, imageUrl }: { name: string; imageUrl: string }) {
  const src = safeImageSrc(imageUrl)

  if (!src) {
    return (
      <div
        aria-hidden="true"
        className="flex aspect-square items-center justify-center rounded-lg bg-slate-100 text-5xl font-semibold text-slate-300"
      >
        {name.charAt(0).toUpperCase()}
      </div>
    )
  }

  return (
    <img
      src={src}
      alt=""
      width={400}
      height={400}
      loading="lazy"
      referrerPolicy="no-referrer"
      className="aspect-square w-full rounded-lg bg-slate-100 object-cover"
    />
  )
}
